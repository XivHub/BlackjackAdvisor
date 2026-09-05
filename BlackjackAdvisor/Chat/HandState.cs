using System.Collections.Generic;

namespace BlackjackAdvisor.Chat
{
    public readonly record struct Card(string Rank, char Suit)
    {
        public int Value => Rank == "A" ? 1 : (Rank is "10" or "J" or "Q" or "K") ? 10 : int.Parse(Rank);
    }

    /// <summary>The hand currently on the table, read from the draw thread and written from
    /// both the draw thread (manual entry) and the chat thread (auto-fill). Every access goes
    /// through the lock so a chat-driven card append can never race a draw-thread enumeration.</summary>
    public sealed class HandState
    {
        private static readonly char[] Suits = { '♠', '♥', '♣', '♦' };

        private readonly object gate = new();
        private readonly List<Card> hand = new();
        private Card? dealer;
        private bool totalMode;
        private int inTotal = 16;
        private bool inSoft;
        private bool inPair;
        private bool filledFromChat;
        private string? dealingTo;
        private bool myTurn;

        public readonly record struct Snapshot(
            IReadOnlyList<Card> Hand, Card? Dealer, bool TotalMode, int InTotal, bool InSoft, bool InPair,
            bool FilledFromChat, string? DealingTo, bool MyTurn);

        /// <summary>A consistent view of every field, taken under one lock, for the draw thread
        /// to evaluate and render without a card being appended mid-read.</summary>
        public Snapshot Read()
        {
            lock (gate)
                return new Snapshot(hand.ToArray(), dealer, totalMode, inTotal, inSoft, inPair, filledFromChat, dealingTo, myTurn);
        }

        public Card? Dealer
        {
            get { lock (gate) return dealer; }
            set { lock (gate) dealer = value; }
        }

        public bool TotalMode
        {
            get { lock (gate) return totalMode; }
            set { lock (gate) totalMode = value; }
        }

        public int InTotal
        {
            get { lock (gate) return inTotal; }
            set { lock (gate) inTotal = value; }
        }

        public bool InSoft
        {
            get { lock (gate) return inSoft; }
            set { lock (gate) inSoft = value; }
        }

        public bool InPair
        {
            get { lock (gate) return inPair; }
            set { lock (gate) inPair = value; }
        }

        public bool FilledFromChat
        {
            get { lock (gate) return filledFromChat; }
            set { lock (gate) filledFromChat = value; }
        }

        public string? DealingTo
        {
            get { lock (gate) return dealingTo; }
            set { lock (gate) dealingTo = value; }
        }

        public bool MyTurn
        {
            get { lock (gate) return myTurn; }
            set { lock (gate) myTurn = value; }
        }

        public int HandCount { get { lock (gate) return hand.Count; } }

        /// <summary>Adds a card with the next cycling suit, exiting total mode. Used for both a
        /// manually clicked rank and a card read off a /random result.</summary>
        public void AddCard(string rank, bool fromChat)
        {
            lock (gate)
            {
                hand.Add(new Card(rank, Suits[hand.Count % 4]));
                totalMode = false;
                filledFromChat = fromChat;
            }
        }

        public bool RemoveLastCard()
        {
            lock (gate)
            {
                if (hand.Count == 0) return false;
                hand.RemoveAt(hand.Count - 1);
                filledFromChat = false;
                return true;
            }
        }

        public void ReplaceHand(IEnumerable<Card> cards)
        {
            lock (gate)
            {
                hand.Clear();
                hand.AddRange(cards);
                totalMode = false;
            }
        }

        public void ClearHand()
        {
            lock (gate)
            {
                hand.Clear();
                totalMode = false;
                filledFromChat = false;
            }
        }

        public void ResetHandAndDealer()
        {
            lock (gate)
            {
                hand.Clear();
                dealer = null;
                totalMode = false;
                filledFromChat = false;
            }
        }

        public void ResetForNextRound()
        {
            lock (gate)
            {
                hand.Clear();
                dealer = null;
                totalMode = false;
                filledFromChat = false;
                dealingTo = null;
            }
        }

        public void SetTotalMode(int total, bool soft, bool pair, bool filledFromChat)
        {
            lock (gate)
            {
                inTotal = total;
                inSoft = soft;
                inPair = pair;
                totalMode = true;
                this.filledFromChat = filledFromChat;
            }
        }

        public int HandTotal(out bool soft)
        {
            lock (gate)
                return TotalOf(hand, out soft);
        }

        // A soft ace (11) drops to 1 as soon as it would otherwise bust the hand.
        public static int TotalOf(IReadOnlyList<Card> cards, out bool soft)
        {
            int total = 0; soft = false;
            foreach (var c in cards)
            {
                int raw = soft ? total - 10 : total;
                raw += c.Value == 1 ? 1 : c.Value;
                bool has = soft || c.Value == 1;
                if (has && raw + 10 <= 21) { total = raw + 10; soft = true; }
                else { total = raw; soft = false; }
            }
            return total;
        }
    }
}
