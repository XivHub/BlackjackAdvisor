using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BlackjackAdvisor.Chat
{
    /// <summary>Every wording template this venue (or every venue) has been bound to a role for.
    /// Matching is the only thing this class does — deciding what a match means is the caller's job.</summary>
    public sealed class TemplateStore
    {
        private const int Cap = 200;

        private readonly object gate = new();
        private readonly List<LearnedLine> lines = new();
        private readonly Dictionary<string, Regex> regexCache = new();
        private readonly Action<string> log;

        public TemplateStore(Action<string> log) => this.log = log;

        /// <summary>Adds a binding, evicting the oldest auto-learned entry when the store is full.
        /// Returns false, without adding, when the cap is full of user-confirmed entries.</summary>
        public bool Add(LearnedLine line)
        {
            lock (gate)
            {
                if (lines.Count >= Cap)
                {
                    var oldest = lines.Where(l => l.Auto).OrderBy(l => l.LearnedAtUnix).FirstOrDefault();
                    if (oldest == null) { log($"store full at {Cap}, refusing '{line.Template}'"); return false; }
                    lines.Remove(oldest);
                    log($"evicted auto line '{oldest.Template}' to make room");
                }
                lines.Add(line);
                return true;
            }
        }

        public void Remove(string template, string dealer)
        {
            lock (gate) lines.RemoveAll(l => l.Template == template && l.Dealer == dealer);
        }

        public void SetRole(string template, string dealer, string role)
        {
            lock (gate)
            {
                var l = lines.FirstOrDefault(x => x.Template == template && x.Dealer == dealer);
                if (l != null) l.Role = role;
            }
        }

        public void SetScope(string template, string dealer, string newDealer)
        {
            lock (gate)
            {
                var l = lines.FirstOrDefault(x => x.Template == template && x.Dealer == dealer);
                if (l != null) l.Dealer = newDealer;
            }
        }

        public IReadOnlyList<LearnedLine> ForDealer(string dealer)
        {
            lock (gate) return lines.Where(l => l.Dealer == dealer).ToList();
        }

        /// <summary>Matches canonicalized text against every stored template, dealer-scoped lines
        /// first (most specific literal wording first), then global lines, ties broken by the
        /// newest binding. A &lt;name&gt; capture that no longer looks like a name fails the match.</summary>
        public (LearnedLine Line, IReadOnlyList<string> Slots)? Find(string canon, string dealer)
        {
            lock (gate)
            {
                var ordered = lines
                    .Where(l => l.Dealer == dealer)
                    .Concat(dealer.Length > 0 ? lines.Where(l => l.Dealer.Length == 0) : Enumerable.Empty<LearnedLine>())
                    .OrderByDescending(l => LineTemplate.LiteralLength(l.Template))
                    .ThenByDescending(l => l.LearnedAtUnix);

                foreach (var candidate in ordered)
                {
                    if (!regexCache.TryGetValue(candidate.Template, out var rx))
                        regexCache[candidate.Template] = rx = LineTemplate.Matcher(candidate.Template);

                    Match match;
                    try { match = rx.Match(canon); }
                    catch (RegexMatchTimeoutException)
                    {
                        log($"template '{candidate.Template}' timed out matching '{canon}'");
                        continue;
                    }
                    if (!match.Success) continue;

                    var kinds = LineTemplate.SlotKinds(candidate.Template);
                    var slots = new List<string>(kinds.Count);
                    bool ok = true;
                    for (int i = 0; i < kinds.Count; i++)
                    {
                        string value = match.Groups[i + 1].Value;
                        if (kinds[i] && !LineTemplate.SlotIsNameShaped(value)) { ok = false; break; }
                        slots.Add(value);
                    }
                    if (!ok) continue;

                    return (candidate, slots);
                }
                return null;
            }
        }
    }
}
