using System;
using System.Collections.Generic;

namespace BlackjackAdvisor.Chat
{
    /// <summary>A maximal run of rolls opened by one dealer line, closed by the dealer's own total
    /// or outcome — or left unconfirmed when neither shows up in time. This is the unit the
    /// checksum learner tests an attribution hypothesis against.</summary>
    public sealed class RollGroup
    {
        public ChatEvent? Opener { get; }
        public List<int> Rolls { get; } = new();
        public bool Confirmed { get; private set; }
        public int? ClosingTotal { get; private set; }
        public bool Bust { get; private set; }

        public RollGroup(ChatEvent? opener) => Opener = opener;

        internal void Close(int? total, bool bust, bool confirmed)
        {
            ClosingTotal = total;
            Bust = bust;
            Confirmed = confirmed;
        }
    }

    /// <summary>Tracks the roll group currently in progress. A run ends the moment a dealer line
    /// that is not pure decoration appears between rolls; from there the group keeps waiting for a
    /// total or outcome for up to 6 dealer lines and 60s past the last roll, closing unconfirmed if
    /// neither arrives in time.</summary>
    public sealed class RollGroupTracker
    {
        private const int MaxDealerLines = 6;
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

        private RollGroup? current;
        private bool runOpen;
        private DateTime lastRollAt;
        private int dealerLinesSinceRunEnded;

        /// <summary>A roll arrived. Returns the previous group if this roll forces it closed
        /// unconfirmed (its run had already ended and it never found a total in time).</summary>
        public RollGroup? OnRoll(Evidence evidence, ChatEvent rollEvent)
        {
            RollGroup? forcedClosed = null;
            if (current != null && !runOpen)
            {
                current.Close(null, false, confirmed: false);
                forcedClosed = current;
                current = null;
            }

            if (current == null)
            {
                current = new RollGroup(evidence.LastCandidateBefore(rollEvent.Seq));
                runOpen = true;
                dealerLinesSinceRunEnded = 0;
            }

            current.Rolls.Add(rollEvent.Roll!.Value);
            lastRollAt = rollEvent.At;
            return forcedClosed;
        }

        /// <summary>A dealer line arrived. Decoration (too general to be a candidate on its own) is
        /// invisible to grouping. Anything else ends the roll run in progress, if any; if it reads
        /// as a total or outcome the group closes confirmed, otherwise the wait bound above keeps
        /// counting until it closes unconfirmed.</summary>
        public RollGroup? OnDealerLine(ChatEvent lineEvent)
        {
            // Decoration here means literally nothing survives canonicalization (a dashed rule, a
            // stray punctuation line) — weaker than the opener-candidate filter in Evidence, since
            // a legitimate closing total ("16") is itself too short/general to ever open a group
            // but must still be able to close one.
            if (string.IsNullOrEmpty(lineEvent.Canon)) return null;
            if (current == null) return null;

            runOpen = false;

            var (total, bust, _) = ChatParser.ReadTotalOrOutcome(lineEvent.Raw);
            if (total.HasValue || bust)
            {
                var g = current;
                g.Close(total, bust, confirmed: true);
                current = null;
                return g;
            }

            dealerLinesSinceRunEnded++;
            if (dealerLinesSinceRunEnded > MaxDealerLines || lineEvent.At - lastRollAt > MaxWait)
            {
                var g = current;
                g.Close(null, false, confirmed: false);
                current = null;
                return g;
            }
            return null;
        }
    }
}
