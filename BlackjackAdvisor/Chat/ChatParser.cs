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
        private string? dealerSender;   // auto-locked dealer sender name
        private string? lastChatText;   // for /bj parse
        private int? houseStandsOn;     // the draw threshold the dealer announced, if they did
        private bool houseHitsSoft;     // whether that announcement implies a soft threshold is hit
        private bool splitHands;        // the local player is holding two hands; which card is in which is unknowable

        public ChatParser(IParserHost host) => this.host = host;

        public HandState State { get; } = new();

        /// <summary>The local player split, so the chat no longer describes one hand this parser can follow.</summary>
        public bool SplitHands => splitHands;

        /// <summary>Clear everything for a fresh round (the ledger buttons, not the chat).</summary>
        public void NextRound()
        {
            splitHands = false;
            State.ResetForNextRound();
        }

        // Whether the cards being dealt right now are the local player's.
        private bool DealingToMe(string me) =>
            State.DealingTo is { } who && who != "Dealer" && ChatText.NameIs(who, me);

        public (int Total, bool HitsSoft)? AnnouncedHouseRule =>
            houseStandsOn is { } n ? (n, houseHitsSoft) : null;

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
            host.Log($"«{line.Kind}» [{sender}] {(text.Length > 100 ? text[..100] : text)}");

            int? roll = presetRoll;

            // Auto-lock the dealer from strong, unmistakable macro markers only.
            bool marker = roll.HasValue
                          || RandomRx.IsMatch(text)
                          || TurnRx.IsMatch(text)
                          || text.Contains("Dealer's Hand", StringComparison.OrdinalIgnoreCase);
            if (marker && !string.IsNullOrEmpty(sender)) dealerSender = ChatText.CleanName(sender);

            HandleLine(text, sender, manual: false, roll);
        }

        /// <summary>Re-runs the last line seen through Feed, ignoring the sender lock. Returns
        /// false when there is no line to parse.</summary>
        public bool ForceParseLast()
        {
            if (string.IsNullOrEmpty(lastChatText)) return false;
            HandleLine(lastChatText, "", manual: true, null);
            return true;
        }

        public IEnumerable<string> StatusLines()
        {
            yield return $"dealer='{Dealer()}'";
            var snap = State.Read();
            yield return $"dealing to='{snap.DealingTo ?? "-"}'  my turn={snap.MyTurn}  "
                + $"hand={(snap.TotalMode ? $"total {snap.InTotal}" : snap.Hand.Count == 0 ? "-" : string.Concat(snap.Hand.Select(h => h.Rank + " ")))} "
                + $"up card={(snap.Dealer?.Rank ?? "-")}";
        }

        private void HandleLine(string text, string sender, bool manual, int? roll)
        {
            var me = host.LocalPlayerName;
            if (string.IsNullOrEmpty(me)) { host.Log("no local player name"); return; }

            bool mentionsHand = text.Contains("hand", StringComparison.OrdinalIgnoreCase);
            var oic = StringComparison.OrdinalIgnoreCase;

            if (manual || SenderIsDealer(sender)) ReadHouseRule(text);

            // A new round: everything from the last one is stale, including the up card.
            if (RoundStartRx.IsMatch(text) && (manual || SenderIsDealer(sender)))
            {
                if (splitHands) host.Log("split hands over");
                splitHands = false;
                State.ResetForNextRound();
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

            // Round start: "Dealing <who>'s Cards" or "Here is your first two Cards <who>!".
            var deal = DealingRx.Match(text);
            if (!deal.Success) deal = FirstCardsRx.Match(text);
            if (deal.Success)
            {
                if (!manual && !SenderIsDealer(sender)) { host.Log($"deal line from '{sender}' != dealer '{Dealer()}'"); return; }
                State.MyTurn = false;
                string who = deal.Groups[1].Value.Trim();
                string dealingToNow = who.Contains("Dealer", oic) ? "Dealer" : who;
                State.DealingTo = dealingToNow;
                bool mineDeal = ChatText.NameIs(who, me);
                // Only clear the hand. The up card is cleared when a new one is coming — the
                // dealer's own reveal below, or a round start — never because a player was dealt:
                // a dealer plays out a split by re-running this same line, mid-round.
                if (mineDeal) State.ClearHand();
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
                State.DealingTo = "Dealer";
                if (reveal.Groups[1].Value.Equals("first", oic)) State.Dealer = null;
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
                State.DealingTo = dealingToNow;
                host.Log($"turn -> myTurn={myTurnNow}, dealingTo={dealingToNow ?? "-"}");
                return;
            }

            // A hand is over, so nobody draws until the next player is prompted.
            if (text.Contains("stays with", oic) || text.Contains("busted", oic)
                || text.Contains("got a blackjack", oic) || text.Contains("has a blackjack", oic))
            {
                State.DealingTo = null;
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
                State.DealingTo = dealingToNow;
                State.MyTurn = myTurnNow;
                host.Log($"{who} -> {act.Groups[2].Value}, myTurn={myTurnNow}");
                return;
            }

            // "<who>, would you like to hit, stand or double down?" — same, plus it may carry the hand.
            var prompt = NamePrefixRx.Match(text);
            if (prompt.Success && (manual || SenderIsDealer(sender)))
            {
                string who = prompt.Groups[1].Value.Trim();
                State.DealingTo = who;
                State.MyTurn = ChatText.NameIs(who, me);
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
                        State.AddCard(RankFromRandom(rv.Value), fromChat: true);
                        host.Log($"preview draw {RankFromRandom(rv.Value)}");
                    }
                    // The up card is the dealer's first draw; later ones are the reveal, not the up card.
                    else if (dealingTo == "Dealer" && State.Dealer == null)
                    {
                        var card = new Card(RankFromRandom(rv.Value), '♠');
                        State.Dealer = card;
                        host.Log($"dealer up {card.Rank}");
                    }
                    else host.Log($"draw {RankFromRandom(rv.Value)} -> {dealingTo}, not you");
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

            ParseAndFill(text);
        }

        // A dealer that prints a hand as a bare number on the line after the draws. The announced
        // total is authoritative: cards that disagree with it mean a draw line was missed.
        private void ApplyBareTotal(Match m, string me)
        {
            bool soft = m.Groups[4].Success;
            int n = int.Parse(soft ? m.Groups[4].Value : m.Groups[1].Value);
            // "14 or 7/7 splits" — the dealer is offering the split, so the hand is that pair.
            bool pair = m.Groups[2].Success && m.Groups[2].Value == m.Groups[3].Value;

            var dealingTo = State.DealingTo;
            if (dealingTo == "Dealer")
            {
                if (State.Dealer == null && n is >= 1 and <= 11)
                {
                    var card = new Card(n is 1 or 11 ? "A" : n.ToString(), '♠');
                    State.Dealer = card;
                    host.Log($"dealer up {card.Rank} (announced)");
                }
                return;
            }

            if (dealingTo == null || !ChatText.NameIs(dealingTo, me) || n is < 2 or > 21) return;

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
            if (houseStandsOn == n && houseHitsSoft == hitsSoft) return;
            houseStandsOn = n;
            houseHitsSoft = hitsSoft;
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

        private void ParseAndFill(string text)
        {
            int di = text.IndexOf("dealer", StringComparison.OrdinalIgnoreCase);
            string playerPart = di >= 0 ? text[..di] : text;
            string dealerPart = di >= 0 ? text[di..] : "";

            var d = ParseDealer(dealerPart);
            var cards = ParseCards(playerPart);
            int? stated = ParseStatedTotal(playerPart);

            if (cards.Count > 0)
            {
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
        private static string RankFromRandom(int n) => n switch
        {
            1 => "A",
            11 => "J",
            12 => "Q",
            13 => "K",
            10 => "10",
            _ => (n >= 2 && n <= 9) ? n.ToString() : "10",
        };
    }
}
