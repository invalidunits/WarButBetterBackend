namespace WarButBetterBackend 
{
    using System.Collections;
    using System.Collections.ObjectModel;
    using System.Runtime.CompilerServices;
    using Card = byte;
    public static class CardExtensions 
    {
        public enum Suite : byte
        {
            Clover = 0,
            Heart,
            Spade,
            Diamond,
            Jester 
        }

        public enum SpecialValues : byte
        {
            Undefined = 0,
            Jack = 11,
            Queen = 12,
            King = 13,
            Ace = 14
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Suite GetSuite(Card card) => (Suite)(card & 0b111);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetValue(Card card) => (card >> 3) & 0b11111;

        public static string CardToString(Card card)
        {
            var suite = GetSuite(card);
            var value = GetValue(card);
            if (suite == Suite.Jester && value == 0) return "Jester";
            if (suite < Suite.Clover || suite >= Suite.Diamond) return "Unknown";
            

            
            if (Enum.GetName((SpecialValues)value) is string value_name)
            {
                return $"{value_name} of {suite}";
            }
            else
            {
                return $"{value} {suite}";
            }
        }
        

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Card GetCard(Suite suite, byte value) => (Card)(((byte)suite & 0b111) | (value << 3) & 0b11111);

        public static readonly ReadOnlyCollection<Card> Standard52Deck = [
            GetCard(Suite.Clover, 2),
            GetCard(Suite.Clover, 3),
            GetCard(Suite.Clover, 4),
            GetCard(Suite.Clover, 5),
            GetCard(Suite.Clover, 6),
            GetCard(Suite.Clover, 7),
            GetCard(Suite.Clover, 8),
            GetCard(Suite.Clover, 9),
            GetCard(Suite.Clover, 10),
            GetCard(Suite.Clover, 11),
            GetCard(Suite.Clover, 12),
            GetCard(Suite.Clover, 13),
            GetCard(Suite.Clover, 14),

            GetCard(Suite.Heart, 2),
            GetCard(Suite.Heart, 3),
            GetCard(Suite.Heart, 4),
            GetCard(Suite.Heart, 5),
            GetCard(Suite.Heart, 6),
            GetCard(Suite.Heart, 7),
            GetCard(Suite.Heart, 8),
            GetCard(Suite.Heart, 9),
            GetCard(Suite.Heart, 10),
            GetCard(Suite.Heart, 11),
            GetCard(Suite.Heart, 12),
            GetCard(Suite.Heart, 13),
            GetCard(Suite.Heart, 14),
            
            GetCard(Suite.Spade, 2),
            GetCard(Suite.Spade, 3),
            GetCard(Suite.Spade, 4),
            GetCard(Suite.Spade, 5),
            GetCard(Suite.Spade, 6),
            GetCard(Suite.Spade, 7),
            GetCard(Suite.Spade, 8),
            GetCard(Suite.Spade, 9),
            GetCard(Suite.Spade, 10),
            GetCard(Suite.Spade, 11),
            GetCard(Suite.Spade, 12),
            GetCard(Suite.Spade, 13),
            GetCard(Suite.Spade, 14),

            GetCard(Suite.Diamond, 2),
            GetCard(Suite.Diamond, 3),
            GetCard(Suite.Diamond, 4),
            GetCard(Suite.Diamond, 5),
            GetCard(Suite.Diamond, 6),
            GetCard(Suite.Diamond, 7),
            GetCard(Suite.Diamond, 8),
            GetCard(Suite.Diamond, 9),
            GetCard(Suite.Diamond, 10),
            GetCard(Suite.Diamond, 11),
            GetCard(Suite.Diamond, 12),
            GetCard(Suite.Diamond, 13),
            GetCard(Suite.Diamond, 14),
        ];
    }

    public class Hand
    {
        public const int HandSize = 5;
        public List<Card> Cards;
        public List<Card> Deck;

        public Hand()
        {        
            Cards = new(HandSize);         
            Deck = new(53);
        }

        public void FillHand()
        {
            if (Cards.Count < HandSize)
            {
                Card[] Added = Deck.Take(HandSize - Cards.Count).ToArray();
                Deck.RemoveRange(0, Added.Count());
                Cards.AddRange(Added);
            }
        }

        public void AddToDeck(IEnumerable<Card> cards)
        {
            lock (this)
            {
                Deck.AddRange(cards);
            }
        }
    }
}