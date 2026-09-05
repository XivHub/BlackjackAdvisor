using System;

namespace BlackjackAdvisor.Chat
{
    /// <summary>One dealer speech line or one roll, as the checksum learner sees it. Mutable only
    /// in <see cref="MatchedRole"/>: a line starts out unclassified and is updated in place once
    /// the learned store (not a built-in — see <see cref="Evidence.LastCandidateBefore"/>) assigns
    /// it a role, so a later lookup knows it is already explained.</summary>
    public sealed class ChatEvent
    {
        public int Seq { get; init; }
        public DateTime At { get; init; }
        public string Sender { get; init; } = "";
        public string Raw { get; init; } = "";
        public string Canon { get; init; } = "";

        /// <summary>The raw 1-13 /random value, unconverted — sum through
        /// <see cref="ChatParser.CardValueFromRandom"/>, never this field directly.</summary>
        public int? Roll { get; init; }

        public TemplateRole? MatchedRole { get; set; }

        /// <summary>The wording template this line reduces to (null for a roll), built against the
        /// known names available at the time it was recorded.</summary>
        public string? MatchedTemplate { get; init; }

        /// <summary>The literal text that filled this line's first &lt;name&gt; slot, if it has
        /// one, in canonical (lowercased, punctuation-stripped) form.</summary>
        public string? Subject { get; init; }
    }

    /// <summary>A 64-entry ring of every dealer speech line and every roll, in order, used to find
    /// the line that opened a roll run and to answer /bj status. Touched only from the ChatParser
    /// thread except for reads, which are locked since /bj status runs from the command thread.</summary>
    public sealed class Evidence
    {
        private const int Capacity = 64;

        private readonly object gate = new();
        private readonly ChatEvent?[] ring = new ChatEvent?[Capacity];
        private int head;   // next write index
        private int count;
        private int nextSeq;

        public ChatEvent Record(DateTime at, string sender, string raw, string canon, int? roll,
            string? matchedTemplate, string? subject)
        {
            lock (gate)
            {
                var ev = new ChatEvent
                {
                    Seq = nextSeq++,
                    At = at,
                    Sender = sender,
                    Raw = raw,
                    Canon = canon,
                    Roll = roll,
                    MatchedTemplate = matchedTemplate,
                    Subject = subject,
                };
                ring[head] = ev;
                head = (head + 1) % Capacity;
                if (count < Capacity) count++;
                return ev;
            }
        }

        /// <summary>The most recent dealer lines (never rolls), oldest first.</summary>
        public System.Collections.Generic.IReadOnlyList<ChatEvent> RecentDealerLines(int n)
        {
            lock (gate)
            {
                var list = new System.Collections.Generic.List<ChatEvent>();
                for (int i = 0; i < count && list.Count < n; i++)
                {
                    var ev = ring[(head - 1 - i + Capacity) % Capacity];
                    if (ev != null && ev.Roll == null) list.Add(ev);
                }
                list.Reverse();
                return list;
            }
        }

        /// <summary>The most recent event before <paramref name="seq"/> that still counts as an
        /// unclaimed candidate opener: a dealer line (not a roll), not already resolved to a role
        /// other than Total by the learned store, and not too general to mean anything on its own
        /// (decoration that canons to nothing skips right over this filter). Never crosses a
        /// RoundStart event — once a template is bound, its own occurrences stop being candidates
        /// (correctly), but the search must not then wander back into the previous round looking
        /// for a substitute and blame an unrelated earlier line for this round's total.</summary>
        public ChatEvent? LastCandidateBefore(int seq)
        {
            lock (gate)
            {
                for (int i = 0; i < count; i++)
                {
                    var ev = ring[(head - 1 - i + Capacity) % Capacity];
                    if (ev == null || ev.Seq >= seq) continue;
                    if (ev.MatchedRole == TemplateRole.RoundStart) return null;
                    if (ev.Roll != null) continue;
                    if (ev.MatchedRole is not (null or TemplateRole.Total)) continue;
                    if (ev.MatchedTemplate == null || LineTemplate.IsTooGeneral(ev.MatchedTemplate)) continue;
                    return ev;
                }
                return null;
            }
        }
    }
}
