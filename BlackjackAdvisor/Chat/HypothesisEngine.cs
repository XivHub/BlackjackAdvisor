using System;
using System.Collections.Generic;
using System.Linq;

namespace BlackjackAdvisor.Chat
{
    /// <summary>Tests a closed roll group against the checksum — the dealer's announced total, or
    /// its lack — to work out which of the four attribution roles a wording template means, and
    /// auto-binds it once two confirmations with differing rolls agree. Also the sanity check that
    /// this table's card values match the standard mapping at all.</summary>
    public sealed class HypothesisEngine
    {
        private readonly IParserHost host;
        private readonly TemplateStore store;
        private readonly Func<bool> learnEnabled;
        private readonly Action markDirty;

        private sealed class HypState
        {
            public int Confirms;
            public readonly HashSet<string> RollMultisets = new();
            public string Example = "";
        }

        // Keyed by the exact wording template and the role being tested for it. More than one role
        // can independently accumulate confirmations for the same template across different groups
        // (their structural conditions can each hold at different points in a round), which is
        // exactly why auto-bind refuses to fire while two roles are tied.
        private readonly Dictionary<(string Template, TemplateRole Role), HypState> hypotheses = new();

        // The cards dealt to each subject this round: "Dealer" for the dealer, else the resolved
        // player identity. Presence of a key is itself meaningful — an absent subject has no
        // defined hand yet this round, which is what tells DealerNext/Acting apart from a fresh
        // DealTo/DealerFirst. Totals run through HandState.TotalOf, the same soft-ace rule the
        // rest of the plugin uses, so a natural blackjack (ace + ten) reads as 21 here too.
        private readonly Dictionary<string, List<Card>> subjectCards = new();

        // Contradictions per bound (template, dealer) — separate from hypotheses, which stops being
        // consulted the moment a template is bound.
        private readonly Dictionary<(string Template, string Dealer), int> contradictions = new();

        // Templates that were unbound this session are never auto-bound again until the plugin restarts.
        private readonly HashSet<(string Template, string Dealer)> sessionVeto = new();

        // Lines bound this session (auto or taught), per dealer scope — what the "learned N of
        // this dealer's lines" note under the table counts. Never persisted: a config already
        // carrying learned lines at startup did not "just" learn them this session.
        private readonly Dictionary<string, int> sessionBinds = new();

        // A wrong binding and a wrong card-value rule both show up as an equation that will not
        // balance, so one disagreement says nothing: only a run of them points at the cards. Below
        // this many samples the question stays open rather than being answered wrongly.
        private const int MinBalanceSample = 6;

        // Learning runs on the chat thread while the window's notes, the learned-lines table and the
        // ledger buttons reach the same dictionaries from the draw thread. A dictionary resized
        // under a concurrent lookup can spin forever, so every collection here is taken under one
        // gate — the same shape TemplateStore uses.
        private readonly object gate = new();

        private readonly Queue<bool> recentBalance = new();
        public bool ValuesLookWrong { get; private set; }

        /// <summary>The last closed group's equation, for /bj status.</summary>
        public string LastEquation { get; private set; } = "-";

        public HypothesisEngine(IParserHost host, TemplateStore store, Func<bool> learnEnabled, Action markDirty)
        {
            this.host = host;
            this.store = store;
            this.learnEnabled = learnEnabled;
            this.markDirty = markDirty;
        }

        /// <summary>Clears the hands a round boundary makes stale. Hypotheses, contradictions and
        /// the veto set persist across rounds — they are about the dealer's wording, not any one
        /// hand.</summary>
        public void ResetForNextRound() { lock (gate) subjectCards.Clear(); }

        /// <summary>What /bj status shows: every hypothesis under test with its confirmation count.</summary>
        public IEnumerable<string> StatusLines()
        {
            // Materialized inside the lock: a lazy projection would be enumerated by the caller
            // long after the gate was released, over a dictionary the chat thread keeps changing.
            lock (gate)
                return hypotheses
                    .Select(kv => $"{RoleIds.Id(kv.Key.Role)} '{kv.Key.Template}' ({kv.Value.Confirms}/2)")
                    .ToList();
        }

        /// <summary>The running total for a subject this round ("Dealer" for the dealer), for
        /// /bj status. Null when that subject has no defined hand yet.</summary>
        public int? RunningTotal(string subject)
        {
            lock (gate)
                return subjectCards.TryGetValue(subject, out var cards) && cards.Count > 0
                    ? HandState.TotalOf(cards, out _) : null;
        }

        /// <summary>How many lines were bound (auto or taught) for this dealer scope since the
        /// plugin started, for the host's "learned N of this dealer's lines" note.</summary>
        public int SessionBindCount(string dealerScope)
        {
            lock (gate) return sessionBinds.TryGetValue(dealerScope, out int n) ? n : 0;
        }

        /// <summary>Adds a (template, dealer) pair to the session veto set directly — used by the
        /// host when a row is removed by hand or a taught binding is undone, so the checksum
        /// learner cannot instantly re-bind what the user just took back.</summary>
        public void Veto(string template, string dealer)
        {
            lock (gate) sessionVeto.Add((template, dealer));
        }

        /// <summary>Counts a binding made outside the checksum path — the teach banner's own
        /// "yes" button — toward the same session tally <see cref="TryAutoBind"/> keeps.</summary>
        public void NoteSessionBind(string dealerScope)
        {
            lock (gate)
            {
                sessionBinds.TryGetValue(dealerScope, out int n);
                sessionBinds[dealerScope] = n + 1;
            }
        }

        public void OnGroupClosed(RollGroup group, IReadOnlyList<string> roster, string dealerScope)
        {
            lock (gate) OnGroupClosedLocked(group, roster, dealerScope);
        }

        private void OnGroupClosedLocked(RollGroup group, IReadOnlyList<string> roster, string dealerScope)
        {
            if (!learnEnabled()) return;
            if (group.Opener == null || group.Bust) return;
            if (!group.Confirmed || group.ClosingTotal is not { } total) return;

            string? template = group.Opener.MatchedTemplate;
            if (template == null || LineTemplate.IsTooGeneral(template)) return;

            bool hasNameSlot = LineTemplate.SlotKinds(template).Contains(true);
            string? subjectSlot = group.Opener.Subject;

            if (hasNameSlot && subjectSlot != null)
            {
                int matches = roster.Count(r => NamesMatch(subjectSlot, r));
                if (matches > 1)
                {
                    host.Log($"subject '{subjectSlot}' is ambiguous on this roster");
                    return;
                }
            }

            string? resolvedSubject = hasNameSlot
                ? roster.FirstOrDefault(r => NamesMatch(subjectSlot!, r)) ?? subjectSlot
                : null;
            bool namesDealer = hasNameSlot && subjectSlot == LineTemplate.Canon(dealerScope);

            // Every roll is summed through RankFromRandom into an actual card, never its raw 1-13
            // value — an 11 is a Jack worth 10, and HandState.TotalOf gives the ace its soft value
            // when that reads a natural blackjack as 21 rather than a raw 11.
            var groupCards = group.Rolls.Select(r => new Card(ChatParser.RankFromRandom(r), '♠')).ToList();
            int freshTotal = HandState.TotalOf(groupCards, out _);
            subjectCards.TryGetValue("Dealer", out var dealerCards);
            subjectCards.TryGetValue(resolvedSubject ?? "", out var subjectExisting);
            int? dealerNextTotal = dealerCards != null ? HandState.TotalOf(dealerCards.Concat(groupCards).ToList(), out _) : null;
            int? actingTotal = subjectExisting != null ? HandState.TotalOf(subjectExisting.Concat(groupCards).ToList(), out _) : null;

            LastEquation = $"{resolvedSubject ?? "Dealer"} + rolls[{string.Join(',', group.Rolls)}] = {total}";

            // A template already bound answers to its own role only — it no longer competes for a
            // fresh hypothesis, but it is still watched for contradictions.
            var bound = store.ForDealer(dealerScope).Concat(store.ForDealer("")).FirstOrDefault(l => l.Template == template);
            if (bound != null && RoleIds.Parse(bound.Role) is { } boundRole)
            {
                TestBoundLine(bound, boundRole, dealerScope, resolvedSubject, groupCards, freshTotal, dealerNextTotal, actingTotal, total);
                return;
            }

            var survivors = new List<TemplateRole>();
            void Test(TemplateRole role, bool applicable, int? predicted)
            {
                if (!applicable || predicted is not { } p) return;
                if (p == total) survivors.Add(role);
                else host.Log($"hyp reject {p} vs {total}");
            }

            Test(TemplateRole.DealTo, group.Rolls.Count == 2 && hasNameSlot, freshTotal);
            Test(TemplateRole.DealerFirst, group.Rolls.Count == 1 && (!hasNameSlot || namesDealer), freshTotal);
            Test(TemplateRole.DealerNext, dealerCards != null && (!hasNameSlot || namesDealer), dealerNextTotal);
            Test(TemplateRole.Acting, hasNameSlot && subjectExisting != null, actingTotal);

            if (survivors.Count != 1)
            {
                host.Log("hyp ambiguous");
                return;
            }

            var winner = survivors[0];
            UpdateRunning(winner, resolvedSubject, groupCards);

            var key = (template, winner);
            if (!hypotheses.TryGetValue(key, out var hs))
            {
                hs = new HypState { Example = group.Opener.Raw };
                hypotheses[key] = hs;
            }
            hs.Confirms++;
            hs.RollMultisets.Add(string.Join(',', group.Rolls.OrderBy(v => v)));

            if (hs.Confirms == 1) host.Log($"hyp form {RoleIds.Id(winner)} '{template}'");
            else host.Log($"hyp confirm ({hs.Confirms}/2) {RoleIds.Id(winner)} '{template}'");

            TryAutoBind(template, winner, hs, dealerScope);
        }

        private void TestBoundLine(LearnedLine bound, TemplateRole boundRole, string dealerScope,
            string? resolvedSubject, List<Card> groupCards, int freshTotal, int? dealerNextTotal, int? actingTotal, int total)
        {
            // DealTo and DealerFirst always open a fresh hand: no prior cards to add to.
            int? predicted = boundRole switch
            {
                TemplateRole.DealTo => freshTotal,
                TemplateRole.DealerFirst => freshTotal,
                TemplateRole.DealerNext => dealerNextTotal,
                TemplateRole.Acting => actingTotal,
                _ => null,
            };

            bool balances = predicted == total;
            RecordBalance(balances);
            if (balances)
            {
                UpdateRunning(boundRole, resolvedSubject, groupCards);
                return;
            }

            host.Log($"hyp reject {(predicted.HasValue ? predicted.Value.ToString() : "-")} vs {total}");
            if (!bound.Auto) return; // user-confirmed lines are never auto-unbound

            var vkey = (bound.Template, dealerScope);
            contradictions.TryGetValue(vkey, out int c);
            contradictions[vkey] = ++c;
            if (c < 3) return;

            store.Remove(bound.Template, bound.Dealer);
            sessionVeto.Add(vkey);
            markDirty();
            contradictions.Remove(vkey);
            // The binding was wrong, not the card values: drop the evidence it produced so a
            // corrected reading is not judged against it.
            recentBalance.Clear();
            if (ValuesLookWrong)
            {
                ValuesLookWrong = false;
                host.Log("card values look consistent with the standard mapping again");
            }
            host.Log($"unbind {RoleIds.Id(boundRole)} '{bound.Template}' after 3 contradictions");
        }

        private void UpdateRunning(TemplateRole role, string? resolvedSubject, List<Card> groupCards)
        {
            string? key = role is TemplateRole.DealerFirst or TemplateRole.DealerNext ? "Dealer" : resolvedSubject;
            if (key == null) return;
            bool fresh = role is TemplateRole.DealTo or TemplateRole.DealerFirst;
            if (fresh || !subjectCards.TryGetValue(key, out var existing))
                subjectCards[key] = new List<Card>(groupCards);
            else
                existing.AddRange(groupCards);
        }

        private void TryAutoBind(string template, TemplateRole role, HypState hs, string dealerScope)
        {
            if (hs.Confirms < 2 || hs.RollMultisets.Count < 2) return;
            if (sessionVeto.Contains((template, dealerScope))) return;

            bool tied = hypotheses.Any(kv => kv.Key.Template == template && kv.Key.Role != role && kv.Value.Confirms >= hs.Confirms);
            if (tied) return;

            var line = new LearnedLine
            {
                Template = template,
                Role = RoleIds.Id(role),
                Dealer = dealerScope,
                Example = hs.Example,
                Auto = true,
                Hits = hs.Confirms,
                LearnedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            if (!store.Add(line)) return;
            markDirty();
            sessionBinds.TryGetValue(dealerScope, out int n);
            sessionBinds[dealerScope] = n + 1;
            host.Log($"bind {RoleIds.Id(role)} '{template}'");
        }

        // Card-value sanity check: if fewer than half of the last 10 confirmed
        // groups balance under the standard mapping, the table's cards likely don't mean what this
        // build assumes. Never fits a model — just raises a flag MainWindow reads.
        // Only a group whose owner is already known says anything about card values: an
        // undecided group is undecided for a hundred reasons that have nothing to do with what a
        // Jack is worth, and counting those suppresses advice at a table that is behaving normally.
        private void RecordBalance(bool balanced)
        {
            recentBalance.Enqueue(balanced);
            while (recentBalance.Count > 10) recentBalance.Dequeue();

            bool wrong = recentBalance.Count >= MinBalanceSample
                         && recentBalance.Count(x => x) * 2 < recentBalance.Count;
            if (wrong == ValuesLookWrong) return;
            ValuesLookWrong = wrong;
            host.Log(wrong
                ? "card values look inconsistent with the standard mapping over the last 10 groups"
                : "card values look consistent with the standard mapping again");
        }

        private static bool NamesMatch(string canonSubject, string fullName) =>
            ChatText.NameForms(fullName).Any(f => LineTemplate.Canon(f) == canonSubject);
    }
}
