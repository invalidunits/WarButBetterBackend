using WarButBetterBackend;
using Card = byte;

namespace WarButBetterTests;

public class CardExtensionsTests
{
    [Fact]
    public void GetCard_ThenDecode_RoundTripsSuiteAndValue()
    {
        Card card = CardExtensions.GetCard(CardExtensions.Suite.Heart, 14);

        Assert.Equal(CardExtensions.Suite.Heart, CardExtensions.GetSuite(card));
        Assert.Equal(14, CardExtensions.GetValue(card));
    }

    [Fact]
    public void CardToString_ForFaceCard_ReturnsNamedValue()
    {
        Card queenOfSpades = CardExtensions.GetCard(CardExtensions.Suite.Spade, 12);

        string label = CardExtensions.CardToString(queenOfSpades);

        Assert.Equal("Queen of Spade", label);
    }

    [Fact]
    public void CardToString_ForNumberCard_ReturnsNumericValue()
    {
        Card sevenOfClovers = CardExtensions.GetCard(CardExtensions.Suite.Clover, 7);

        string label = CardExtensions.CardToString(sevenOfClovers);

        Assert.Equal("7 Clover", label);
    }

    [Fact]
    public void CardToString_ForJesterZero_ReturnsJester()
    {
        Card jester = CardExtensions.GetCard(CardExtensions.Suite.Jester, 0);

        string label = CardExtensions.CardToString(jester);

        Assert.Equal("Jester", label);
    }

    [Fact]
    public void CardToString_ForDiamondFaceCard_ReturnsNamedValue()
    {
        Card aceOfDiamonds = CardExtensions.GetCard(CardExtensions.Suite.Diamond, 14);

        string label = CardExtensions.CardToString(aceOfDiamonds);

        Assert.Equal("Ace of Diamond", label);
    }

    [Fact]
    public void Standard52Deck_Has52UniqueCards()
    {
        IReadOnlyCollection<Card> deck = CardExtensions.Standard52Deck;

        Assert.Equal(52, deck.Count);
        Assert.Equal(52, deck.Distinct().Count());
    }
}

public class HandTests
{
    [Fact]
    public void FillHand_DrawsUpToHandSize_FromDeckFront()
    {
        var hand = new Hand();
        hand.AddToDeck([
            CardExtensions.GetCard(CardExtensions.Suite.Clover, 2),
            CardExtensions.GetCard(CardExtensions.Suite.Clover, 3),
            CardExtensions.GetCard(CardExtensions.Suite.Clover, 4),
            CardExtensions.GetCard(CardExtensions.Suite.Clover, 5),
            CardExtensions.GetCard(CardExtensions.Suite.Clover, 6),
            CardExtensions.GetCard(CardExtensions.Suite.Clover, 7)
        ]);

        hand.FillHand();

        Assert.Equal(Hand.HandSize, hand.Cards.Count);
        Assert.Single(hand.Deck);
        Assert.Equal(CardExtensions.GetCard(CardExtensions.Suite.Clover, 7), hand.Deck[0]);
    }

    [Fact]
    public void FillHand_DoesNothing_WhenHandAlreadyFull()
    {
        var hand = new Hand();
        hand.Cards.AddRange([
            CardExtensions.GetCard(CardExtensions.Suite.Heart, 2),
            CardExtensions.GetCard(CardExtensions.Suite.Heart, 3),
            CardExtensions.GetCard(CardExtensions.Suite.Heart, 4),
            CardExtensions.GetCard(CardExtensions.Suite.Heart, 5),
            CardExtensions.GetCard(CardExtensions.Suite.Heart, 6)
        ]);
        hand.AddToDeck([
            CardExtensions.GetCard(CardExtensions.Suite.Spade, 10)
        ]);

        hand.FillHand();

        Assert.Equal(Hand.HandSize, hand.Cards.Count);
        Assert.Single(hand.Deck);
        Assert.Equal(CardExtensions.GetCard(CardExtensions.Suite.Spade, 10), hand.Deck[0]);
    }
}
