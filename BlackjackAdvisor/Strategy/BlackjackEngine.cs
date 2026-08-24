using System;
using System.Collections.Generic;

namespace BlackjackAdvisor.Strategy
{
    public enum Move { Stand, Hit, Double, Split }

    /// <summary>One playable option and its expected value (in units of the base bet).</summary>
    public readonly record struct OptionEV(Move Move, double EV, bool Available);

    public sealed class EvalResult
    {
        public int Total;
        public bool Soft;
        public bool Bust;
        public bool Blackjack;
        public bool IsPair;
        public List<OptionEV> Options = new();
        public Move Best;
        public bool HasBest;
    }

    /// <summary>
    /// Exact basic-strategy solver for FFXIV-style blackjack.
    ///
    /// In the in-game variant every card is a uniform /random 1-13 draw with 10/J/Q/K = 10 and
    /// Ace = 1/11. Nothing is removed between draws, so the distribution over card *values* is
    /// A=1/13, 2..9=1/13 each, 10-value=4/13 — identical to an infinite-deck shoe. That makes the
    /// optimal move exactly computable by expected value (no strategy-table transcription, no card
    /// counting, deck count irrelevant). Rule variants (dealer soft-17, double-after-split) are
    /// parameters, so the same engine covers every host's ruleset.
    /// </summary>
    public sealed class BlackjackEngine
    {
        private readonly bool h17; // dealer hits soft 17 (vs stands)
        private readonly bool das; // double after split allowed

        // Draw probability by card value 1..10 (1 = Ace, 10 = any ten-value card).
        private static readonly double[] P = BuildProbs();

        private static double[] BuildProbs()
        {
            var p = new double[11];
            for (int v = 1; v <= 9; v++) p[v] = 1.0 / 13.0;
            p[10] = 4.0 / 13.0; // 10, J, Q, K
            return p;
        }

        // Rule-dependent, query-independent: cache across the whole session.
        private readonly Dictionary<int, double[]> dealerMemo = new();

        // Query-dependent (fixed dealer up card): cleared each Evaluate.
        private readonly Dictionary<int, double> hitMemo = new();
        private double[] dd = Array.Empty<double>(); // current dealer final-total distribution

        public BlackjackEngine(bool dealerHitsSoft17, bool doubleAfterSplit)
        {
            h17 = dealerHitsSoft17;
            das = doubleAfterSplit;
        }

        /// <summary>Dealer's final-total distribution from an up card (index 0 = bust, 17..21 = total). For inspection/tests.</summary>
        public double[] DealerDistributionFromUp(int up)
        {
            var s = Add(0, false, up);
            return (double[])DealerDist(s.total, s.soft).Clone();
        }

        private static int Key(int total, bool soft) => (total << 1) | (soft ? 1 : 0);

        /// <summary>Add a card of value v to (total, soft), returning the normalized hand.</summary>
        private static (int total, bool soft) Add(int total, bool soft, int v)
        {
            int raw = soft ? total - 10 : total; // sum with any ace counted as 1
            raw += (v == 1 ? 1 : v);
            bool hasAce = soft || v == 1;
            if (hasAce && raw + 10 <= 21) return (raw + 10, true);
            return (raw, false); // raw may exceed 21 => bust
        }

        private bool DealerStands(int total, bool soft)
        {
            if (total >= 18) return true;
            if (total == 17) return soft ? !h17 : true; // hard 17 always; soft 17 only if S17
            return false;
        }

        /// <summary>
        /// Distribution of the dealer's final total starting from hand (total, soft).
        /// Index 0 = bust; indices 17..21 = that final total.
        /// </summary>
        private double[] DealerDist(int total, bool soft)
        {
            if (total > 21) return Bust();
            int key = Key(total, soft);
            if (dealerMemo.TryGetValue(key, out var cached)) return cached;

            var res = new double[22];
            if (DealerStands(total, soft))
            {
                res[total] = 1.0;
            }
            else
            {
                for (int v = 1; v <= 10; v++)
                {
                    var (nt, ns) = Add(total, soft, v);
                    if (nt > 21) { res[0] += P[v]; continue; }
                    var sub = DealerDist(nt, ns);
                    for (int i = 0; i < res.Length; i++) res[i] += P[v] * sub[i];
                }
            }
            dealerMemo[key] = res;
            return res;

            static double[] Bust() { var b = new double[22]; b[0] = 1.0; return b; }
        }

        /// <summary>EV of standing on playerTotal against the current dealer distribution.</summary>
        private double StandEV(int playerTotal)
        {
            if (playerTotal > 21) return -1.0;
            double ev = dd[0]; // dealer busts => +1
            for (int dt = 17; dt <= 21; dt++)
            {
                if (dt < playerTotal) ev += dd[dt];
                else if (dt > playerTotal) ev -= dd[dt];
                // equal => push, +0
            }
            return ev;
        }

        /// <summary>EV of hitting (total, soft) and then playing optimally (stand or hit).</summary>
        private double HitEV(int total, bool soft)
        {
            int key = Key(total, soft);
            if (hitMemo.TryGetValue(key, out var cached)) return cached;

            double ev = 0;
            for (int v = 1; v <= 10; v++)
            {
                var (nt, ns) = Add(total, soft, v);
                double val = nt > 21 ? -1.0 : Math.Max(StandEV(nt), HitEV(nt, ns));
                ev += P[v] * val;
            }
            hitMemo[key] = ev;
            return ev;
        }

        /// <summary>EV of doubling a 2-card (total, soft): one card, then stand, stakes doubled.</summary>
        private double DoubleEV(int total, bool soft)
        {
            double ev = 0;
            for (int v = 1; v <= 10; v++)
            {
                var (nt, _) = Add(total, soft, v);
                double val = nt > 21 ? -2.0 : 2.0 * StandEV(nt);
                ev += P[v] * val;
            }
            return ev;
        }

        /// <summary>EV of splitting a pair of the given card value (1 = aces). Resplitting ignored.</summary>
        private double SplitEV(int pairValue)
        {
            // Split aces: exactly one card per hand, no further play.
            if (pairValue == 1)
            {
                var start = Add(0, false, 1); // (11, true)
                double perAce = 0;
                for (int v = 1; v <= 10; v++)
                {
                    var (nt, _) = Add(start.total, start.soft, v);
                    perAce += P[v] * (nt > 21 ? -1.0 : StandEV(nt));
                }
                return 2.0 * perAce;
            }

            var s0 = Add(0, false, pairValue); // (value, false)
            double perHand = 0;
            for (int v = 1; v <= 10; v++)
            {
                var (nt, ns) = Add(s0.total, s0.soft, v);
                double best = Math.Max(StandEV(nt), HitEV(nt, ns));
                if (das) best = Math.Max(best, DoubleEV(nt, ns));
                perHand += P[v] * best;
            }
            return 2.0 * perHand;
        }

        /// <summary>
        /// Evaluate a known hand (card values 1..10, 1 = Ace) against a dealer up card (1..10).
        /// hostAllowsDouble/Split reflect whether the host offers those actions at all.
        /// </summary>
        public EvalResult Evaluate(IReadOnlyList<int> hand, int dealerUp, bool hostAllowsDouble, bool hostAllowsSplit)
        {
            int total = 0;
            bool soft = false;
            foreach (var c in hand)
            {
                var (t, s) = Add(total, soft, c);
                total = t; soft = s;
            }
            bool twoCards = hand.Count == 2;
            bool isPair = twoCards && hand[0] == hand[1];
            return Core(total, soft, dealerUp,
                canDouble: twoCards && hostAllowsDouble,
                isPair: isPair, pairValue: isPair ? hand[0] : 0,
                canSplit: hostAllowsSplit,
                blackjack: twoCards && total == 21);
        }

        /// <summary>
        /// Evaluate from a bare total (for dealers that announce only a number, no cards).
        /// Pair value is inferred from the total. Double is treated as available if the host
        /// offers it, since card count is unknown.
        /// </summary>
        public EvalResult EvaluateTotal(int total, bool soft, bool isPair, int dealerUp, bool hostAllowsDouble, bool hostAllowsSplit)
        {
            int pairValue = 0;
            if (isPair)
            {
                if (soft && total == 12) pairValue = 1;         // A,A
                else if (total % 2 == 0) pairValue = total / 2; // pair of (total/2)
                else isPair = false;                            // odd total can't be a pair
            }
            return Core(total, soft, dealerUp,
                canDouble: hostAllowsDouble, isPair: isPair, pairValue: pairValue,
                canSplit: hostAllowsSplit, blackjack: false);
        }

        private EvalResult Core(int total, bool soft, int dealerUp, bool canDouble, bool isPair, int pairValue, bool canSplit, bool blackjack)
        {
            var r = new EvalResult
            {
                Total = total,
                Soft = soft,
                Bust = total > 21,
                IsPair = isPair,
                Blackjack = blackjack,
            };

            // Dealer distribution for this up card; reset the per-query cache.
            var upStart = Add(0, false, dealerUp);
            dd = DealerDist(upStart.total, upStart.soft);
            hitMemo.Clear();

            if (r.Bust) return r; // nothing to decide

            r.Options.Add(new OptionEV(Move.Stand, StandEV(total), true));
            r.Options.Add(new OptionEV(Move.Hit, total >= 21 ? StandEV(total) : HitEV(total, soft), total < 21));
            r.Options.Add(new OptionEV(Move.Double, canDouble ? DoubleEV(total, soft) : 0, canDouble));
            r.Options.Add(new OptionEV(Move.Split, (isPair && canSplit) ? SplitEV(pairValue) : 0, isPair && canSplit));

            double bestEV = double.NegativeInfinity;
            foreach (var o in r.Options)
            {
                if (!o.Available) continue;
                if (o.EV > bestEV) { bestEV = o.EV; r.Best = o.Move; r.HasBest = true; }
            }
            return r;
        }
    }
}
