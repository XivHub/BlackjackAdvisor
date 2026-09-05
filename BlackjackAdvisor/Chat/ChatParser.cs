using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BlackjackAdvisor.Chat
{
    /// <summary>Reads a dealer's chat lines and fills in the local player's hand, up card and
    /// totals. Holds no reference to Dalamud, ImGui or ECommons — everything it needs from the
    /// running game arrives through <see cref="IParserHost"/> and <see cref="ChatLine"/>.</summary>
    public sealed class ChatParser
    {
        private readonly IParserHost host;
        private readonly TemplateStore store;
        private readonly Evidence evidence = new();
        private readonly RollGroupTracker rollGroups = new();
        private readonly HypothesisEngine hypotheses;
        private readonly bool noBuiltins;
        private volatile bool learnedDirty;
        private string? dealerSender;   // auto-locked dealer sender name
        private string? lastChatText;   // for /bj parse
        private DateTime lastAt;        // for /bj parse
        // The draw threshold the dealer announced, written on the chat thread and read on the draw
        // thread to offer a one-click rules change. Packed into one word (-1 = unheard, the sign
        // carries "hits a soft one") so a reader can never see half of an update and offer a
        // threshold the dealer never said.
        private volatile int houseRule = -1;
        private bool splitHands;        // the local player is holding two hands; which card is in which is unknowable
        private DateTime? dealingToSetAt;    // when DealingTo last changed or last saw an attributed roll

        // Unclaimed-roll tracking (see PendingUnclaimedRanks): a run of rolls that arrived while
        // DealingTo was null, so nothing — learned or built-in — could ever have assigned them to a
        // hand. Written from the chat thread (Feed), read and cleared from the draw thread (the
        // teach banner and the manual card buttons), so this alone among the teach-flow fields
        // needs a lock; everything else below it only ever runs on the draw thread.
        private readonly object unclaimedGate = new();
        private readonly List<int> unclaimedRolls = new();
        private DateTime unclaimedLastAt;
        private int unclaimedOpenerSeq;
        private static readonly TimeSpan UnclaimedTimeout = TimeSpan.FromSeconds(45);

        // Manually-entered card values noted through NoteManualCard since the hand was last
        // cleared — kept separately from HandState because a hand can mix manually-clicked and
        // chat-filled cards, and only the manual ones are ever compared against an unclaimed group.
        // Card values the user clicked in since the hand was last cleared, by chat or by hand.
        private readonly List<int> manualHandValues = new();

        // The teach-banner proposal: up to 3 candidate opener lines to try, most likely first,
        // offered one at a time by DrawTeachBanner. Draw-thread only.
        private readonly List<ChatEvent> teachCandidates = new();
        private int teachIndex;
        private TemplateRole teachRole;
        private bool teachExhausted;
        private LearnedLine? lastTaughtLine;
        private DateTime lastTaughtAt;
        private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

        /// <summary>noBuiltins disables the built-in attribution regexes below the learned-store
        /// lookup — the wording guesses a venue-specific macro would otherwise need. Roll
        /// recognition, bare-total/outcome recognition and the safety net stay active either way:
        /// they are fixed game-format constants the checksum itself depends on, never something a
        /// venue improvises.</summary>
        public ChatParser(IParserHost host, bool noBuiltins = false)
        {
            this.host = host;
            this.noBuiltins = noBuiltins;
            store = new TemplateStore(msg => host.Log(msg));
            hypotheses = new HypothesisEngine(host, store, () => host.LearnDealerWording, () => learnedDirty = true);
        }

        public HandState State { get; } = new();

        /// <summary>The learned wording bindings this parser consults ahead of its built-in
        /// regexes. Empty until something adds to it — nothing in this parser learns on its own.</summary>
        public TemplateStore Store => store;

        /// <summary>The checksum learner: hypotheses under test, auto-bind, unbind-on-contradiction
        /// and the card-value sanity check.</summary>
        public HypothesisEngine Hypotheses => hypotheses;

        /// <summary>Set by the learner whenever it binds, unbinds, or otherwise changes
        /// <see cref="Store"/>. The host flushes it to disk from the draw thread, at most once a
        /// frame — never from the chat thread this flag is set on.</summary>
        public bool LearnedDirty => learnedDirty;
        public void ClearLearnedDirty() => learnedDirty = false;

        /// <summary>The local player split, so the chat no longer describes one hand this parser can follow.</summary>
        public bool SplitHands => splitHands;

        /// <summary>Clear everything for a fresh round (the ledger buttons, not the chat).</summary>
        public void NextRound()
        {
            splitHands = false;
            State.ResetForNextRound();
            hypotheses.ResetForNextRound();
            manualHandValues.Clear();
            lock (unclaimedGate) unclaimedRolls.Clear();
            ResetTeach();
        }

        // Whether the cards being dealt right now are the local player's.
        private bool DealingToMe(string me) =>
            State.DealingTo is { } who && who != "Dealer" && ChatText.NameIs(who, me);

        public (int Total, bool HitsSoft)? AnnouncedHouseRule =>
            houseRule is var packed && packed >= 0 ? (packed & 0xFF, (packed & 0x100) != 0) : null;

        /// <summary>The cleaned dealer scope this parser currently binds and matches learned lines
        /// under — the same key <see cref="Store"/> and <see cref="Hypotheses"/> use, exposed so
        /// the host's "Learned dealer lines" UI stays keyed identically.</summary>
        public string DealerScope => ChatText.CleanName(Dealer());

        // ---- Learned-line editing (host UI: Rules -> Learned dealer lines) ---------------------

        /// <summary>Changes a bound line's role in place, e.g. from the row's role combo.</summary>
        public void SetLearnedRole(string template, string dealer, string roleId)
        {
            store.SetRole(template, dealer, roleId);
            learnedDirty = true;
        }

        /// <summary>Removes one bound line and vetoes it for the rest of the session, so the
        /// checksum learner cannot re-bind what the user just took back.</summary>
        public void RemoveLearnedLine(string template, string dealer)
        {
            store.Remove(template, dealer);
            hypotheses.Veto(template, dealer);
            learnedDirty = true;
        }

        /// <summary>"Forget every line for &lt;dealer&gt;" — removes and vetoes every line scoped
        /// to that dealer.</summary>
        public void RemoveAllLearnedLinesFor(string dealer)
        {
            foreach (var l in store.ForDealer(dealer))
            {
                store.Remove(l.Template, l.Dealer);
                hypotheses.Veto(l.Template, l.Dealer);
            }
            learnedDirty = true;
        }

        // ---- Teach banner: unclaimed rolls and the candidate-line proposal ---------------------

        /// <summary>One candidate wording the teach banner is offering to bind, and the role its
        /// arming context implies (fixed for the life of one proposal — only the wording tried
        /// changes as candidates are rejected).</summary>
        public readonly record struct TeachCandidate(TemplateRole Role, string Template, string RawLine);

        /// <summary>Card ranks from the most recent run of rolls that arrived with no attribution
        /// target at all, within the last 45s — nothing in this parser, learned or built-in, could
        /// have told whose these were. Null once claimed, dismissed, replaced by a newer run, or stale.</summary>
        public IReadOnlyList<string>? PendingUnclaimedRanks
        {
            get
            {
                lock (unclaimedGate)
                {
                    if (unclaimedRolls.Count == 0 || DateTime.Now - unclaimedLastAt > UnclaimedTimeout) return null;
                    return unclaimedRolls.Select(RankFromRandom).ToList();
                }
            }
        }

        /// <summary>"Those were my cards": replaces the local hand with the unclaimed group — it
        /// reads as an opening deal, since it is standing in for the two-card line that should have
        /// filled the hand on its own — and arms a teach proposal for the line that opened it.</summary>
        public void ClaimUnclaimedRolls()
        {
            List<int> rolls;
            int openerSeq;
            lock (unclaimedGate)
            {
                if (unclaimedRolls.Count == 0) return;
                rolls = new List<int>(unclaimedRolls);
                openerSeq = unclaimedOpenerSeq;
                unclaimedRolls.Clear();
            }
            State.ReplaceHand(rolls.Select(r => new Card(RankFromRandom(r), '♠')).ToList());
            State.FilledFromChat = true;
            host.Log($"claimed unclaimed rolls [{string.Join(',', rolls)}] as your hand");
            ArmTeach(TemplateRole.DealTo, openerSeq);
        }

        /// <summary>"Not mine": drops the unclaimed group without touching the hand or proposing anything.</summary>
        public void DismissUnclaimedRolls()
        {
            lock (unclaimedGate) unclaimedRolls.Clear();
        }

        /// <summary>Called from the "Add to your hand" buttons in DrawControls with the value just
        /// clicked. When everything noted this hand matches an unclaimed group as a multiset of
        /// card values, arms a teach proposal — a fresh two-card match reads as an opening deal, a
        /// single-card match onto an already-noted hand reads as a hit.</summary>
        public void NoteManualCard(int value)
        {
            manualHandValues.Add(value);
            List<int> rolls;
            int openerSeq;
            lock (unclaimedGate)
            {
                if (unclaimedRolls.Count == 0) return;
                if (!MultisetEquals(manualHandValues, unclaimedRolls.Select(CardValueFromRandom))) return;
                rolls = new List<int>(unclaimedRolls);
                openerSeq = unclaimedOpenerSeq;
                unclaimedRolls.Clear();
            }
            var role = rolls.Count == 2 ? TemplateRole.DealTo : TemplateRole.Acting;
            host.Log($"manual entry matches unclaimed rolls [{string.Join(',', rolls)}]");
            manualHandValues.Clear();
            ArmTeach(role, openerSeq);
        }

        /// <summary>Called from the dealer up-card buttons in DrawControls with the value just
        /// clicked. This widget only ever holds one card, so a match always reads as the dealer's
        /// first card.</summary>
        public void NoteManualDealerCard(int value)
        {
            List<int> rolls;
            int openerSeq;
            lock (unclaimedGate)
            {
                if (unclaimedRolls.Count != 1 || CardValueFromRandom(unclaimedRolls[0]) != value) return;
                rolls = new List<int>(unclaimedRolls);
                openerSeq = unclaimedOpenerSeq;
                unclaimedRolls.Clear();
            }
            host.Log($"manual dealer entry matches unclaimed roll [{rolls[0]}]");
            ArmTeach(TemplateRole.DealerFirst, openerSeq);
        }

        /// <summary>Forgets everything noted through <see cref="NoteManualCard"/> — called when the
        /// hand is cleared or undone by hand, so a stale note never falsely matches a later group.</summary>
        public void ClearManualEntries() => manualHandValues.Clear();

        /// <summary>The proposal currently offered, or null when there is none (either nothing has
        /// matched yet, or every candidate for the current match has been rejected).</summary>
        public TeachCandidate? PendingTeach =>
            teachIndex < teachCandidates.Count
                ? new TeachCandidate(teachRole, teachCandidates[teachIndex].MatchedTemplate!, teachCandidates[teachIndex].Raw)
                : null;

        /// <summary>True for one match's whole lifetime once its candidates run out with none
        /// accepted — the banner's "No other line to try" state.</summary>
        public bool TeachExhausted => teachExhausted;

        /// <summary>"No, not that line": tries the next candidate, if any.</summary>
        public void RejectTeachCandidate()
        {
            if (teachIndex >= teachCandidates.Count) return;
            teachIndex++;
            if (teachIndex >= teachCandidates.Count)
            {
                teachExhausted = true;
                teachCandidates.Clear();
            }
        }

        /// <summary>"Not now": walks away from this match without vetoing anything — the same
        /// wording is free to propose itself again on a later match.</summary>
        public void DismissTeach() => ResetTeach();

        /// <summary>The role-specific "yes" button: binds the offered candidate as a user-confirmed
        /// (never auto-unbound) line and arms the 10s undo window.</summary>
        public void AcceptTeach()
        {
            if (PendingTeach is not { } cand) return;
            string dealer = DealerScope;
            var line = new LearnedLine
            {
                Template = cand.Template,
                Role = RoleIds.Id(cand.Role),
                Dealer = dealer,
                Example = cand.RawLine,
                Auto = false,
                Hits = 0,
                LearnedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            if (store.Add(line))
            {
                lastTaughtLine = line;
                lastTaughtAt = DateTime.Now;
                hypotheses.NoteSessionBind(dealer);
                learnedDirty = true;
                host.Log($"bind {RoleIds.Id(cand.Role)} '{cand.Template}' (taught)");
            }
            ResetTeach();
        }

        public bool CanUndoTeach => lastTaughtLine != null && DateTime.Now - lastTaughtAt <= UndoWindow;

        /// <summary>Undoes a just-taught binding within its 10s window and vetoes it for the rest
        /// of the session, so the checksum learner cannot instantly re-bind what was just taken back.</summary>
        public void UndoTeach()
        {
            if (lastTaughtLine is not { } l) return;
            store.Remove(l.Template, l.Dealer);
            hypotheses.Veto(l.Template, l.Dealer);
            learnedDirty = true;
            lastTaughtLine = null;
        }

        // Loads up to 3 candidate opener lines before the roll that started the matched group,
        // most likely first, and offers the first one. Sets TeachExhausted directly when there is
        // nothing to try at all — a match with no plausible preceding wording is still worth
        // telling the user about, once, rather than silently doing nothing.
        private void ArmTeach(TemplateRole role, int openerSeq)
        {
            teachCandidates.Clear();
            teachCandidates.AddRange(evidence.CandidatesBefore(openerSeq, 3));
            teachIndex = 0;
            teachRole = role;
            teachExhausted = teachCandidates.Count == 0;
        }

        private void ResetTeach()
        {
            teachCandidates.Clear();
            teachIndex = 0;
            teachExhausted = false;
        }

        private static int CardValueFromRandom(int roll) => new Card(RankFromRandom(roll), '♠').Value;

        private static bool MultisetEquals(IReadOnlyCollection<int> a, IEnumerable<int> b)
        {
            var counts = new Dictionary<int, int>();
            foreach (int v in a) counts[v] = counts.GetValueOrDefault(v) + 1;
            int remaining = a.Count;
            foreach (int v in b)
            {
                if (!counts.TryGetValue(v, out int c) || c == 0) return false;
                counts[v] = c - 1;
                remaining--;
            }
            return remaining == 0;
        }

        // Card token in either order, case-insensitive, T or 10 for ten, optional space.
        private static readonly Regex CardRx = new(
            @"(?:([♣♠♦♥])\s*(10|[2-9]|[TtAaJjQqKk])|(10|[2-9]|[TtAaJjQqKk])\s*([♣♠♦♥]))", RegexOptions.Compiled);
        private static readonly Regex AceGlyphRx = new(@"[♣♠♦♥]\s*[Aa]|[Aa]\s*[♣♠♦♥]", RegexOptions.Compiled);
        // Prefer an explicit "Total: N"; the gap is only spaces/colons so it never skips over a card glyph to a rank digit.
        private static readonly Regex TotalKwRx = new(@"total[\s:]*(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HandNumRx = new(@"(?:hand(?:\s+is)?|have)[\s:]*(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DealerTokenRx = new(@"([♣♠♦♥])\s*(10|[2-9]|[TtAaJjQqKk])|(10|[2-9]|[TtAaJjQqKk])\s*([♣♠♦♥])|\b(10|[2-9]|[TtAaJjQqKk])\b", RegexOptions.Compiled);
        // The dealer addresses one player: "<name>, your hand is ..." / "<name>, would you like to ...".
        private static readonly Regex NamePrefixRx = new(
            @"^\s*([^,]+?),\s*(?:your\s+hand|would\s+you\s+like|what\s+would\s+you\s+like|do\s+you\s+want)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TurnRx = new(@"'s Turn", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DealingRx = new(@"Dealing\s+(.+?)'s\s+Cards", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // "Here is your first two Cards <name>!" — the name trails the line instead of taking a possessive.
        private static readonly Regex FirstCardsRx = new(@"\bfirst\s+two\s+cards?\b[:\s]*(.+?)[\s!?]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // "Time to reveal the Dealer's first Card!" — some macros drop the name ("the 's first Card").
        private static readonly Regex RevealRx = new(@"\breveal\b[^.!?]*?\b(first|second|next)\s+card", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // "<name> chooses to Double Down!" / "<name> chooses Hit!" / "<name> is forced to Stand!"
        private static readonly Regex ActionRx = new(
            @"^\s*(.+?)\s+(?:chooses|choose|decides|opts|wants|is\s+forced)(?:\s+to)?\s+(hit|stand|double|split)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A hand announced as a number on its own line: "15", "1 or 11", "1/11", "Blackjack 16",
        // or with the pair a dealer offers to split: "14 or 7/7 splits". An ace is written both
        // ways, so the last number is the soft reading either way.
        private static readonly Regex BareTotalRx = new(
            @"^[\s\-–—]*(?:blackjack|total|score|hand)?[\s:!.]*(\d{1,2})"
            + @"(?:\s*or\s*(\d{1,2})\s*/\s*(\d{1,2})\s*splits?|\s*(?:or|/)\s*(\d{1,2}))?\s*[.!]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Two totals on one line ("13 - 9", "bust - 19") is a player holding two hands after a
        // split. Which cards belong to which hand is not recoverable from the draws alone, so this
        // is a signal to stop guessing rather than something to parse.
        private static readonly Regex SplitTotalsRx = new(
            @"^\s*(\d{1,2}|bust(?:ed)?)\s*[-–—]\s*(\d{1,2}|bust(?:ed)?)\s*[.!]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // The macro line that opens a round, before anyone is dealt.
        private static readonly Regex RoundStartRx = new(
            @"\b(?:all\s+players\s+have\s+placed|new\s+round\s+begins|place\s+your\s+bets|thank\s+you\s+for\s+playing)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A /random result: "(1-13) 9", tolerant of locale prefix (Random!/Würfeln!), en/em dashes and spacing.
        private static readonly Regex RandomRx = new(@"\(\s*\d{1,2}\s*[-–—]\s*\d{1,2}\s*\)\s*(\d{1,2})", RegexOptions.Compiled);
        // "<name> rolls a 5" style (some tables/RP dealers).
        private static readonly Regex RollsRx = new(@"\brolls?\s+(?:a\s+)?(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DigitRunRx = new(@"\d+", RegexOptions.Compiled);
        // Dealers state the house rule in passing: "DEALER is below 16 and rolls again.",
        // "Dealer stands on 17". Worth reading — the draw threshold moves the advice on every
        // stiff hand, and it is the one rule a player is least likely to think to ask about.
        private static readonly Regex HouseDrawsRx = new(
            @"\bbelow\s+(\d{1,2})\b.{0,20}?\b(?:rolls?|draws?|hits?)\s+again", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HouseStandsRx = new(
            @"\bstands?\s+(?:on|at)\s+(?:soft\s+|hard\s+|all\s+)?(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SummaryRx = new(@"([^,]+?)'s\s+hand\s+is\s+(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A natural blackjack is always worth 21; a bust carries no total at all. Both are chat-format
        // constants the checksum learner reads regardless of --no-builtins, same as BareTotalRx.
        private static readonly Regex OutcomeBlackjackRx = new(@"\b(?:got|has)\s+a\s+blackjack\b|blackjack!", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OutcomeBustRx = new(@"\bbusted\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Attribution safety net: bounds on how long, and how far, a guess is trusted.
        private static readonly TimeSpan DealingToTimeout = TimeSpan.FromSeconds(45);
        private const int MaxHandCards = 12;

        /// <summary>Whether the text itself looks like a /random or "rolls a N" result — used by
        /// the host to fill in ChatLine.IsRandomRoll before the line even reaches the parser.</summary>
        internal static bool IsRollText(string text) => RandomRx.IsMatch(text) || RollsRx.IsMatch(text);

        /// <summary>Last integer in a string, fullwidth-normalized for the JP client. Exposed so
        /// the host can pair it with a chat type it recognises as a genuine /random result.</summary>
        internal static int? LastNumber(string text)
        {
            var s = ChatText.NormalizeDigits(text);
            var ms = DigitRunRx.Matches(s);
            if (ms.Count == 0) return null;
            return int.TryParse(ms[^1].Value, out int v) ? v : null;
        }

        /// <summary>Feeds one chat line to the parser. presetRoll carries a value already read
        /// from a chat type the host trusts as a genuine roll result, ahead of any text pattern.</summary>
        public void Feed(ChatLine line, int? presetRoll = null)
        {
            string text = line.Text, sender = line.Sender;
            lastChatText = text;
            lastAt = line.At;
            host.Log($"«{line.Kind}» [{sender}] {(text.Length > 100 ? text[..100] : text)}");

            // Only speech and roll results are ever dealer wording or a player's die roll; every
            // other channel (buffs, item use, system messages) carries no attribution signal and,
            // unlike speech, can show a bystander's name with an empty sender — never mistaken for
            // a hand-relevant line, but never worth even trying to classify as one either.
            if (!line.IsSpeech && !line.IsRandomRoll)
            {
                host.Log("dropped: not speech or a roll");
                return;
            }

            int? roll = presetRoll;

            // Auto-lock the dealer from strong, unmistakable macro markers only.
            bool marker = roll.HasValue
                          || RandomRx.IsMatch(text)
                          || TurnRx.IsMatch(text)
                          || text.Contains("Dealer's Hand", StringComparison.OrdinalIgnoreCase);
            if (marker && !string.IsNullOrEmpty(sender)) dealerSender = ChatText.CleanName(sender);

            // Every dealer speech line and every roll feeds the checksum learner, independent of
            // whatever the dispatch below makes of it. A roll's sender need not be the dealer (some
            // venues have the acting player roll for themselves).
            int? rollValue = line.IsRandomRoll ? (roll ?? RollValueFromText(text)) : null;
            ChatEvent? ev = null;
            if (rollValue.HasValue || (line.IsSpeech && SenderIsDealer(sender)))
                ev = RecordEvidence(sender, text, rollValue, line.At);

            // DealingTo as of just before this line dispatches: a roll that arrives while it is
            // still null is one HandleLine's own "if (dealingTo != null)" gate below will never
            // pick up either, built-in or learned — that is what makes it genuinely unclaimed
            // rather than merely unresolved for a frame.
            // Only the dealer's rolls are candidates. "rolls a 5" is ordinary speech, and a loot
            // roll or another table's /random must never raise "unclaimed cards" over the felt.
            if (rollValue.HasValue && (SenderIsDealer(sender) || string.IsNullOrEmpty(sender)))
                NoteRollForUnclaimed(rollValue.Value, line.At, ev!);

            HandleLine(text, sender, manual: false, roll, line.At, ev);
        }

        // Extends or restarts the unclaimed-roll run this roll belongs to. A roll that arrives once
        // an attribution target is already known ends any run in progress — the table has moved on.
        private void NoteRollForUnclaimed(int rollValue, DateTime at, ChatEvent ev)
        {
            lock (unclaimedGate)
            {
                if (State.DealingTo != null) { unclaimedRolls.Clear(); return; }
                if (unclaimedRolls.Count > 0 && at - unclaimedLastAt > UnclaimedTimeout) unclaimedRolls.Clear();
                if (unclaimedRolls.Count == 0) unclaimedOpenerSeq = ev.Seq;
                unclaimedRolls.Add(rollValue);
                unclaimedLastAt = at;
            }
        }

        // Builds this line's wording template against the names currently known (roster, the local
        // player, and the dealer), records it into the evidence ring, and feeds the roll-group
        // tracker — closing a group, if this line closes one, into the hypothesis engine.
        private ChatEvent RecordEvidence(string sender, string text, int? rollValue, DateTime at)
        {
            string canon = LineTemplate.Canon(text);
            string? template = null, subject = null;
            if (!rollValue.HasValue)
            {
                var known = KnownNameCanonForms();
                var (tpl, slots) = LineTemplate.Templatize(canon, known);
                template = tpl;
                var kinds = LineTemplate.SlotKinds(tpl);
                for (int i = 0; i < kinds.Count; i++)
                    if (kinds[i]) { subject = slots[i]; break; }
            }

            var ev = evidence.Record(at, sender, text, canon, rollValue, template, subject);
            string dealerScope = ChatText.CleanName(Dealer());
            RollGroup? closed = rollValue.HasValue ? rollGroups.OnRoll(evidence, ev) : rollGroups.OnDealerLine(ev);
            if (closed != null) hypotheses.OnGroupClosed(closed, host.RosterNames, dealerScope);
            return ev;
        }

        // The local player plus the roster — every player name this parser can put a canon slot
        // back into its proper chat rendering for.
        private List<string> KnownFullNames(string me)
        {
            var full = new List<string>();
            if (!string.IsNullOrEmpty(me)) full.Add(me);
            full.AddRange(host.RosterNames);
            return full;
        }

        // Canonicalized forms (including abbreviations) of every name this parser currently knows,
        // for Templatize to recognise inside a dealer line's wording.
        private IEnumerable<string> KnownNameCanonForms()
        {
            var full = KnownFullNames(host.LocalPlayerName ?? "");
            string dealer = Dealer();
            if (!string.IsNullOrEmpty(dealer)) full.Add(dealer);
            return full.SelectMany(ChatText.NameForms).Select(LineTemplate.Canon).Distinct();
        }

        /// <summary>Re-runs the last line seen through Feed, ignoring the sender lock. Returns
        /// false when there is no line to parse.</summary>
        public bool ForceParseLast()
        {
            if (string.IsNullOrEmpty(lastChatText)) return false;
            HandleLine(lastChatText, "", manual: true, null, lastAt);
            return true;
        }

        public IEnumerable<string> StatusLines()
        {
            yield return $"dealer='{Dealer()}'";
            var snap = State.Read();
            yield return $"dealing to='{snap.DealingTo ?? "-"}'  my turn={snap.MyTurn}  "
                + $"hand={(snap.TotalMode ? $"total {snap.InTotal}" : snap.Hand.Count == 0 ? "-" : string.Concat(snap.Hand.Select(h => h.Rank + " ")))} "
                + $"up card={(snap.Dealer?.Rank ?? "-")}";

            string dealerScope = ChatText.CleanName(Dealer());
            var bound = store.ForDealer(dealerScope);
            yield return $"learned: {bound.Count} bound line(s) for '{dealerScope}'";
            foreach (var h in hypotheses.StatusLines()) yield return $"  hyp {h}";

            string subjectKey = snap.DealingTo ?? "-";
            // subjectCards is keyed by roster name when the roster resolved one, else the raw
            // canon slot text — try both so status has a chance of finding it either way.
            int? running = subjectKey == "-" ? null
                : hypotheses.RunningTotal(subjectKey) ?? hypotheses.RunningTotal(LineTemplate.Canon(subjectKey));
            yield return $"subject='{subjectKey}' running={(running.HasValue ? running.Value.ToString() : "-")}";
            yield return $"last group: {hypotheses.LastEquation}";
            if (hypotheses.ValuesLookWrong) yield return "values look wrong: yes";
        }

        // Assigns DealingTo and (re)starts its 45s attribution clock. Clearing it (who == null)
        // clears the clock too, so a fresh target always gets the full timeout.
        private void SetDealingTo(string? who, DateTime at)
        {
            if (State.DealingTo == who) return;
            State.DealingTo = who;
            dealingToSetAt = who == null ? null : at;
        }

        // A roll was successfully attributed to whatever DealingTo currently names — the target is
        // still live, so the attribution clock restarts from here.
        private void TouchDealingTo(DateTime at) => dealingToSetAt = at;

        // A hand that runs past 21 ends the local player's turn even without an explicit "busted"
        // line from the dealer — some macros never send one.
        private void CheckBust(DateTime at)
        {
            if (State.HandCount == 0) return;
            int total = State.HandTotal(out _);
            if (total <= 21) return;
            host.Log($"hand busts at {total} -> turn over");
            SetDealingTo(null, at);
            State.MyTurn = false;
        }

        private void HandleLine(string text, string sender, bool manual, int? roll, DateTime at, ChatEvent? ev = null)
        {
            var me = host.LocalPlayerName;
            if (string.IsNullOrEmpty(me)) { host.Log("no local player name"); return; }

            // A guess this stale is worse than no guess: nothing has confirmed it in 45s.
            if (State.DealingTo != null && dealingToSetAt is { } setAt && at - setAt > DealingToTimeout)
            {
                host.Log($"dealingTo '{State.DealingTo}' expired after {DealingToTimeout.TotalSeconds:0}s with no attributed roll");
                SetDealingTo(null, at);
            }

            bool mentionsHand = text.Contains("hand", StringComparison.OrdinalIgnoreCase);
            var oic = StringComparison.OrdinalIgnoreCase;

            if (manual || SenderIsDealer(sender)) ReadHouseRule(text);

            // A new round: everything from the last one is stale, including the up card. Marked on
            // its own evidence event so it is never mistaken for an unclaimed candidate opener —
            // this recognition runs even under --no-builtins, so it must not leak into the checksum.
            if (RoundStartRx.IsMatch(text) && (manual || SenderIsDealer(sender)))
            {
                if (ev != null) evidence.SetRole(ev, TemplateRole.RoundStart);
                if (splitHands) host.Log("split hands over");
                splitHands = false;
                State.ResetForNextRound();
                hypotheses.ResetForNextRound();
                dealingToSetAt = null;
                // Only the lock-guarded field is safe to touch from this (chat) thread — the teach
                // banner's own candidate/undo state is draw-thread-only and is left to go stale
                // until the ledger's own NextRound() or a fresh match supersedes it.
                lock (unclaimedGate) unclaimedRolls.Clear();
                host.Log("round start");
                return;
            }

            // Two totals on one line: this player is holding split hands from here on.
            if (SplitTotalsRx.IsMatch(text) && (manual || SenderIsDealer(sender)))
            {
                if (!splitHands && DealingToMe(me))
                {
                    splitHands = true;
                    State.ClearHand();
                    host.Log($"split hands ({text.Trim()}) - auto-fill off until the next round");
                }
                return;
            }

            // Learned dealer wording outranks every built-in below: a hit dispatches its role and
            // returns, and an Ignore binding vetoes whatever built-in would otherwise have fired.
            if (manual || SenderIsDealer(sender))
            {
                string canon = LineTemplate.Canon(text);
                var hit = store.Find(canon, ChatText.CleanName(Dealer()));
                if (hit is { } h)
                {
                    // Slots come back matched against canon (lowercased, punctuation stripped).
                    // Prefer the exact rendering a known name takes ("Mira K.", not "mira k" —
                    // canon throws the abbreviation's period away); fall back to a best-effort
                    // reconstruction of the dealer's own text for names nobody here knows.
                    var known = KnownFullNames(me);
                    var slots = h.Slots
                        .Select(s => ChatText.BestDisplayForm(s, known) ?? LineTemplate.RecoverOriginalCase(text, s))
                        .ToList();
                    DispatchLearned(h.Line, slots, me, at, ev);
                    return;
                }
            }

            // Everything below is a built-in attribution guess — a wording pattern this build
            // assumes rather than one the dealer taught it — and is skipped entirely under
            // --no-builtins so a fixture can prove the learned store alone reproduces it.
            Match prompt = Match.Empty;
            if (!noBuiltins)
            {
                // Round start: "Dealing <who>'s Cards" or "Here is your first two Cards <who>!".
                var deal = DealingRx.Match(text);
                if (!deal.Success) deal = FirstCardsRx.Match(text);
                if (deal.Success)
                {
                    if (!manual && !SenderIsDealer(sender)) { host.Log($"deal line from '{sender}' != dealer '{Dealer()}'"); return; }
                    State.MyTurn = false;
                    string who = deal.Groups[1].Value.Trim();
                    string dealingToNow = who.Contains("Dealer", oic) ? "Dealer" : who;
                    SetDealingTo(dealingToNow, at);
                    bool mineDeal = ChatText.NameIs(who, me);
                    // Only clear the hand. The up card is cleared when a new one is coming — the
                    // dealer's own reveal below, or a round start — never because a player was dealt:
                    // a dealer plays out a split by re-running this same line, mid-round.
                    if (mineDeal) { State.ClearHand(); manualHandValues.Clear(); }
                    if (dealingToNow == "Dealer") State.Dealer = null;
                    host.Log($"dealing to {dealingToNow}{(mineDeal ? " (you)" : $", you are {me}")}");
                    return;
                }

                // The dealer's own card is drawn next; a first-card reveal starts a fresh up card.
                var reveal = RevealRx.Match(text);
                if (reveal.Success)
                {
                    if (!manual && !SenderIsDealer(sender)) return;
                    State.MyTurn = false;
                    SetDealingTo("Dealer", at);
                    bool first = reveal.Groups[1].Value.Equals("first", oic);
                    if (first) State.Dealer = null;
                    host.Log("dealer reveal");
                    return;
                }

                // Turn header: capture the dealer's up-card draw; stop previewing on player turns.
                if (!mentionsHand && text.Contains("Turn", oic))
                {
                    bool dealerTurn = text.Contains("Dealer", oic);
                    bool myTurnNow = !dealerTurn && ChatText.NameMentioned(text, me);
                    string? dealingToNow = dealerTurn ? "Dealer" : null;
                    State.MyTurn = myTurnNow;
                    SetDealingTo(dealingToNow, at);
                    host.Log($"turn -> myTurn={myTurnNow}, dealingTo={dealingToNow ?? "-"}");
                    return;
                }

                // A hand is over, so nobody draws until the next player is prompted.
                if (text.Contains("stays with", oic) || text.Contains("busted", oic)
                    || text.Contains("got a blackjack", oic) || text.Contains("has a blackjack", oic))
                {
                    SetDealingTo(null, at);
                    State.MyTurn = false;
                    return;
                }

                // "<who> chooses to Hit!" — that player receives the cards drawn next.
                var act = ActionRx.Match(text);
                if (act.Success && (manual || SenderIsDealer(sender)))
                {
                    string who = act.Groups[1].Value.Trim();
                    bool ended = act.Groups[2].Value.StartsWith("stand", oic);
                    string? dealingToNow = ended ? null : who;
                    bool myTurnNow = !ended && ChatText.NameIs(who, me);
                    SetDealingTo(dealingToNow, at);
                    State.MyTurn = myTurnNow;
                    host.Log($"{who} -> {act.Groups[2].Value}, myTurn={myTurnNow}");
                    return;
                }

                // "<who>, would you like to hit, stand or double down?" — same, plus it may carry the hand.
                prompt = NamePrefixRx.Match(text);
                if (prompt.Success && (manual || SenderIsDealer(sender)))
                {
                    string who = prompt.Groups[1].Value.Trim();
                    SetDealingTo(who, at);
                    State.MyTurn = ChatText.NameIs(who, me);
                }
            }

            var dealingTo = State.DealingTo;
            if (splitHands)
            {
                host.Log("ignored while split hands are in play");
                return;
            }

            if (dealingTo != null)
            {
                // Preview: a /random draw. Prefer the chat-type value, then text fallbacks.
                int? rv = roll ?? RollValueFromText(text);
                if (rv is >= 1 and <= 13)
                {
                    if (!manual && !SenderIsDealer(sender) && !string.IsNullOrEmpty(sender))
                    { host.Log($"draw from '{sender}' != dealer '{Dealer()}'"); return; }
                    if (ChatText.NameIs(dealingTo, me))
                    {
                        if (State.HandCount >= MaxHandCards)
                        {
                            host.Log($"refusing draw: hand already has {MaxHandCards} cards");
                            return;
                        }
                        State.AddCard(RankFromRandom(rv.Value), fromChat: true);
                        TouchDealingTo(at);
                        host.Log($"preview draw {RankFromRandom(rv.Value)}");
                        CheckBust(at);
                    }
                    // The up card is the dealer's first draw; later ones are the reveal, not the up card.
                    else if (dealingTo == "Dealer" && State.Dealer == null)
                    {
                        var card = new Card(RankFromRandom(rv.Value), '♠');
                        State.Dealer = card;
                        TouchDealingTo(at);
                        host.Log($"dealer up {card.Rank}");
                    }
                    else
                    {
                        TouchDealingTo(at);
                        host.Log($"draw {RankFromRandom(rv.Value)} -> {dealingTo}, not you");
                    }
                    return;
                }

                // Bare number announcing the hand just dealt ("15", "1 or 11").
                var bare = BareTotalRx.Match(text);
                if (bare.Success)
                {
                    if (!manual && !SenderIsDealer(sender)) return;
                    ApplyBareTotal(bare, me);
                    return;
                }
            }

            if (noBuiltins) return;

            // Preview fallback: "<me>'s hand is N" summary (used only if no cards were captured).
            if (mentionsHand && !text.Contains("your hand", oic))
            {
                var sum = SummaryRx.Match(text);
                if (sum.Success && ChatText.NameIs(sum.Groups[1].Value, me))
                {
                    if (!manual && !SenderIsDealer(sender)) return;
                    if (State.HandCount == 0 && int.TryParse(sum.Groups[2].Value, out int tn) && tn is >= 2 and <= 21)
                    {
                        State.SetTotalMode(tn, text.Contains("or 11", oic), false, filledFromChat: true);
                        host.Log($"preview total {tn}");
                    }
                    return;
                }
            }

            // Hand-line trigger: literal "your hand", or the decision prompt.
            bool handLine = text.Contains("your hand", oic)
                            || (text.Contains("hit", oic) && text.Contains("stand", oic));
            if (!handLine) return;

            // Sender must be the dealer (auto-locked or configured), unless forced.
            if (!manual && !SenderIsDealer(sender))
            {
                host.Log($"skip: sender '{sender}' != dealer '{Dealer()}'");
                return;
            }

            // Ownership: name prefix wins, else current turn.
            bool mine = manual || (prompt.Success ? ChatText.NameIs(prompt.Groups[1].Value, me) : State.MyTurn);
            if (!mine) { host.Log("skip: not my hand"); return; }

            ParseAndFill(text, at);
        }

        // Dispatches a matched learned template. Learned bindings outrank every built-in and an
        // Ignore binding is a veto: the caller returns unconditionally on any store hit.
        private void DispatchLearned(LearnedLine line, IReadOnlyList<string> slots, string me, DateTime at, ChatEvent? ev)
        {
            var roleOpt = RoleIds.Parse(line.Role);
            if (roleOpt is null) host.Log($"learned line '{line.Template}' has unknown role '{line.Role}' -> treated as ignore");
            var role = roleOpt ?? TemplateRole.Ignore;
            var kinds = LineTemplate.SlotKinds(line.Template);
            string subject = "-";

            switch (role)
            {
                case TemplateRole.DealTo:
                {
                    subject = FirstNameSlot(kinds, slots);
                    State.MyTurn = false;
                    SetDealingTo(subject, at);
                    // Only clear the hand. The up card is cleared when a new one is coming — the
                    // dealer's own reveal, or a round start — never because a player was dealt:
                    // a dealer plays out a split by re-running this same line, mid-round. Mirrors
                    // the built-in deal path exactly.
                    if (ChatText.NameIs(subject, me)) { State.ClearHand(); manualHandValues.Clear(); }
                    break;
                }
                case TemplateRole.DealerFirst:
                    subject = "Dealer";
                    State.MyTurn = false;
                    SetDealingTo("Dealer", at);
                    State.Dealer = null;
                    break;
                case TemplateRole.DealerNext:
                    subject = "Dealer";
                    State.MyTurn = false;
                    SetDealingTo("Dealer", at);
                    break;
                case TemplateRole.Acting:
                    subject = FirstNameSlot(kinds, slots);
                    SetDealingTo(subject, at);
                    State.MyTurn = ChatText.NameIs(subject, me);
                    break;
                case TemplateRole.EndTurn:
                    SetDealingTo(null, at);
                    State.MyTurn = false;
                    break;
                case TemplateRole.Total:
                {
                    string? namedSubject = null, lastNumSlot = null;
                    for (int i = 0; i < kinds.Count && i < slots.Count; i++)
                    {
                        if (kinds[i]) namedSubject ??= slots[i];
                        else lastNumSlot = slots[i];
                    }
                    string? target = namedSubject ?? State.DealingTo;
                    subject = target ?? "-";
                    if (lastNumSlot != null && LastNumber(lastNumSlot) is { } n)
                        ApplyTotal(target, n, soft: false, pair: false, me);
                    break;
                }
                case TemplateRole.RoundStart:
                    if (splitHands) host.Log("split hands over");
                    splitHands = false;
                    State.ResetForNextRound();
                    hypotheses.ResetForNextRound();
                    dealingToSetAt = null;
                    lock (unclaimedGate) unclaimedRolls.Clear();
                    break;
                case TemplateRole.Ignore:
                default:
                    break;
            }

            // The four checksum roles must stay eligible candidate openers even once bound — the
            // learner re-verifies a bound line's arithmetic on every later occurrence,
            // which needs its own group to still resolve an Opener for it. Only the roles outside
            // the checksum's four (EndTurn, Total, RoundStart, Ignore) are ever "explained" here.
            if (ev != null && role is not (TemplateRole.DealTo or TemplateRole.DealerFirst
                or TemplateRole.DealerNext or TemplateRole.Acting))
                evidence.SetRole(ev, role);
            host.Log($"learned {RoleIds.Id(role)} '{line.Template}' -> {subject}");
        }

        // A dealer that prints a hand as a bare number on the line after the draws. The announced
        // total is authoritative: cards that disagree with it mean a draw line was missed.
        private void ApplyBareTotal(Match m, string me)
        {
            bool soft = m.Groups[4].Success;
            int n = int.Parse(soft ? m.Groups[4].Value : m.Groups[1].Value);
            // "14 or 7/7 splits" — the dealer is offering the split, so the hand is that pair.
            bool pair = m.Groups[2].Success && m.Groups[2].Value == m.Groups[3].Value;
            ApplyTotal(State.DealingTo, n, soft, pair, me);
        }

        // The value half of ApplyBareTotal, shared with the learned Total role dispatch: given who
        // the total is for (built-ins always pass State.DealingTo; a learned template that names
        // its subject passes that name instead), apply n as the dealer's up card or the subject's hand.
        private void ApplyTotal(string? subject, int n, bool soft, bool pair, string me)
        {
            if (subject == "Dealer")
            {
                if (State.Dealer == null && n is >= 1 and <= 11)
                {
                    var card = new Card(n is 1 or 11 ? "A" : n.ToString(), '♠');
                    State.Dealer = card;
                    host.Log($"dealer up {card.Rank} (announced)");
                }
                return;
            }

            if (subject == null || !ChatText.NameIs(subject, me) || n is < 2 or > 21) return;

            if (State.HandCount > 0 && !pair)
            {
                int high = State.HandTotal(out bool isSoft);
                if (n == high || n == (isSoft ? high - 10 : high)) return;
                host.Log($"cards total {high}, dealer said {n} -> total mode");
            }
            State.SetTotalMode(n, soft, pair, filledFromChat: true);
        }

        // "below 16 and rolls again" is a threshold: they draw under it and stand on it, soft or not.
        // "stands on 17" names the same threshold from the other side.
        private void ReadHouseRule(string text)
        {
            var m = HouseDrawsRx.Match(text);
            bool hitsSoft = false;
            if (!m.Success)
            {
                m = HouseStandsRx.Match(text);
                hitsSoft = m.Success && text.Contains("hits soft", StringComparison.OrdinalIgnoreCase);
            }
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n) || n is < 12 or > 21) return;
            int packed = n | (hitsSoft ? 0x100 : 0);
            if (houseRule == packed) return;
            houseRule = packed;
            host.Log($"house rule announced: stands on {n}{(hitsSoft ? ", hits soft" : "")}");
        }

        private string Dealer() => !string.IsNullOrWhiteSpace(host.ConfiguredDealerName) ? host.ConfiguredDealerName : dealerSender ?? "";

        private bool SenderIsDealer(string sender)
        {
            if (!string.IsNullOrWhiteSpace(host.ConfiguredDealerName))
                return ChatText.CleanName(sender).StartsWith(ChatText.CleanName(host.ConfiguredDealerName), StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(dealerSender)) return true; // not locked yet -> allow bootstrap
            return ChatText.SameSpeaker(sender, dealerSender);
        }

        private void ParseAndFill(string text, DateTime at)
        {
            int di = text.IndexOf("dealer", StringComparison.OrdinalIgnoreCase);
            string playerPart = di >= 0 ? text[..di] : text;
            string dealerPart = di >= 0 ? text[di..] : "";

            var d = ParseDealer(dealerPart);
            var cards = ParseCards(playerPart);
            int? stated = ParseStatedTotal(playerPart);

            if (cards.Count > 0)
            {
                if (cards.Count > MaxHandCards)
                {
                    host.Log($"refusing {cards.Count}-card hand (cap {MaxHandCards})");
                    return;
                }
                // With real cards, only an explicit "Total: N" may override them (self-heal on parse gaps).
                int computed = HandState.TotalOf(cards, out _);
                var tk = TotalKwRx.Match(playerPart);
                if (tk.Success && int.TryParse(tk.Groups[1].Value, out int st) && st is >= 2 and <= 21 && st != computed)
                {
                    host.Log($"card/total mismatch (cards={computed}, stated={st}) -> total mode");
                    SetTotal(st, playerPart, d);
                    return;
                }
                State.ReplaceHand(cards);
                if (d.HasValue) State.Dealer = d.Value;
                State.FilledFromChat = true;
                host.Log($"cards {string.Concat(cards.Select(x => x.Rank))} vs dealer {(d?.Rank ?? "?")}");
                CheckBust(at);
                return;
            }

            if (stated.HasValue) { SetTotal(stated.Value, playerPart, d); host.Log($"total {stated} vs dealer {(d?.Rank ?? "?")}"); }
            else host.Log("no hand data parsed");
        }

        private void SetTotal(int total, string playerPart, Card? d)
        {
            bool soft = playerPart.Contains("or 11", StringComparison.OrdinalIgnoreCase) || AceGlyphRx.IsMatch(playerPart);
            State.SetTotalMode(Math.Clamp(total, 2, 21), soft, false, filledFromChat: true);
            if (d.HasValue) State.Dealer = d.Value;
        }

        private static List<Card> ParseCards(string s)
        {
            var list = new List<Card>();
            foreach (Match m in CardRx.Matches(s))
            {
                if (m.Groups[1].Success) list.Add(new Card(NormalizeRank(m.Groups[2].Value), m.Groups[1].Value[0]));
                else list.Add(new Card(NormalizeRank(m.Groups[3].Value), m.Groups[4].Value[0]));
            }
            return list;
        }

        private static Card? ParseDealer(string dealerPart)
        {
            if (string.IsNullOrEmpty(dealerPart)) return null;
            if (dealerPart.Contains("or 11", StringComparison.OrdinalIgnoreCase)) return new Card("A", '♠');
            var m = DealerTokenRx.Match(dealerPart);
            if (!m.Success) return null;
            if (m.Groups[1].Success) return new Card(NormalizeRank(m.Groups[2].Value), m.Groups[1].Value[0]);
            if (m.Groups[3].Success) return new Card(NormalizeRank(m.Groups[3].Value), m.Groups[4].Value[0]);
            return new Card(NormalizeRank(m.Groups[5].Value), '♠');
        }

        private static int? ParseStatedTotal(string playerPart)
        {
            var m = TotalKwRx.Match(playerPart);
            if (!m.Success) m = HandNumRx.Match(playerPart);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int t) && t is >= 2 and <= 21) return t;
            return null;
        }

        // Result value from /random text: "(1-13) 9" or "<name> rolls a 9".
        private static int? RollValueFromText(string text)
        {
            var m = RandomRx.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v)) return v;
            var r = RollsRx.Match(text);
            if (r.Success && int.TryParse(r.Groups[1].Value, out int v2)) return v2;
            return null;
        }

        private static string NormalizeRank(string r)
        {
            r = r.ToUpperInvariant();
            return r is "T" or "J" or "Q" or "K" ? "10" : r;
        }

        // A /random 1-13 draw -> card rank (1=A, 11/12/13 = J/Q/K, all worth 10).
        // A role says what kind of event a line is; its name slot says whose. A template can carry
        // numbers before the name ("dealing <n> cards over to <name>"), so the subject is the first
        // <name> slot rather than whichever slot happens to come first.
        private static string FirstNameSlot(IReadOnlyList<bool> kinds, IReadOnlyList<string> slots)
        {
            for (int i = 0; i < kinds.Count && i < slots.Count; i++)
                if (kinds[i]) return slots[i];
            return "-";
        }

        internal static string RankFromRandom(int n) => n switch
        {
            1 => "A",
            11 => "J",
            12 => "Q",
            13 => "K",
            10 => "10",
            _ => (n >= 2 && n <= 9) ? n.ToString() : "10",
        };

        /// <summary>Whether a dealer line announces a hand's outcome the checksum can close a roll
        /// group against: a natural blackjack (always 21), a bust (no total), or a bare total.
        /// Independent of whose turn is tracked — this is chat-format recognition, not a venue's
        /// wording, so it runs the same with or without --no-builtins.</summary>
        internal static (int? Total, bool Bust, string Shape) ReadTotalOrOutcome(string text)
        {
            if (OutcomeBlackjackRx.IsMatch(text)) return (21, false, "blackjack");
            if (OutcomeBustRx.IsMatch(text)) return (null, true, "bust");
            var bare = BareTotalRx.Match(text);
            if (bare.Success)
            {
                bool soft = bare.Groups[4].Success;
                int n = int.Parse(soft ? bare.Groups[4].Value : bare.Groups[1].Value);
                return (n, false, "bare total");
            }
            return (null, false, "");
        }
    }
}
