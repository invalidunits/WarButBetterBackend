using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/*
2 player game, each player has a hand of 5 cards and a deck of 54 cards, split into 27 random cards for each player. Each card is given an effect and wins against any card of a lower rank. Players can play any card in their hand each turn. Once a player's deck is fully depleted, that player loses.

Rules for cards

Highest card capttures. If a card has an effect that captures the opposing card, It overrides unless...

The other card has a similar effect against our card,
War: If both placed cards are identical, both players place 3 of their remaining cards in hand and place their 4th on top, the higher card takes every card currently placed. If both top cards are equal again, draw top 4 cards from each and repeat. If both players don't have enough cards for a war it's a tie. If one player doesn't have enough cards for a war, the other wins.

You lose if

If you have no remaining cards.
If you have no cards to sacrifice for war
If you only have jokers
Each card effect:

Joker: Burns any card from the game permanently. (self destructs) ( Placed upside down and becomes effectless in war )
2: If captured, Force the other player to place first next turn.
3: If captured, Choose a card from the top 5 cards in your deck.
4: Shuffle the opponents deck.
5: If this card loses, swap any card from your hand with a card from the opponents (you do not see their)
6: Always captures anything in it's suite
7: Always captures 9
8: No effect
9: Can be played as a 6.
10: Disables any card effect.
Jack: If Jack is played in war, it always captures.
Queen: If captured grab a random King from your Deck If no King is available, this card has no effect
King: Both players play a random card next turn (top card in deck)
Ace: You always capture (unless it's a joker), but any card effects apply whether you win or lose.
*/

namespace WarButBetterBackend
{
    using Card = byte;
    public partial class Match
    {
        public enum MatchState 
        {
            WaitingForPlayers,
            StartingGame,
            RunningGame
        };


        public volatile int Turn = 0;
        public MatchState State { get; private set; }
        public Task? Game { get; private set; }
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        public int ConnectedPlayerCount
        {
            get
            {
                lock (_sync)
                {
                    int count = 0;
                    for (int i = 0; i < _Players.Length; i++)
                        if (_Players[i].client != null) count++;
                    return count;
                }
            }
        }

        public Match()
        {
            State = MatchState.WaitingForPlayers;
            _Players = [(null, new Hand()), (null, new Hand())];
            _Clients = new(_Players.Length);

            cancellationSource = new CancellationTokenSource();
            _forceDeckPlayNextTurn = new bool[_Players.Length];
            _playedCards = [0, 0];
            _cardDetails = [
                new(2,  EffectTrigger.WhenLoser,  CreateEvent: ctx => new ForceWinnerFirstEvent(this, ctx)),
                new(3,  EffectTrigger.WhenLoser,  CreateEvent: ctx => new ChooseFromTopFiveEvent(this, ctx)),
                new(4,  EffectTrigger.WhenWinner, CreateEvent: ctx => new ShuffleOpponentDeckEvent(this, ctx)),
                new(5,  EffectTrigger.WhenLoser,  CreateEvent: ctx => new SwapHandCardEvent(this, ctx)),
                new(12, EffectTrigger.WhenLoser,  CreateEvent: ctx => new QueenGrabKingEvent(this, ctx)),
                new(13, EffectTrigger.WhenEither, CreateEvent: ctx => new ForceDeckPlayEvent(this, ctx)),

                new(6,  EffectTrigger.WhenEither, OneUP: ctx =>
                    CardExtensions.GetSuite(ctx.myCard) == CardExtensions.GetSuite(ctx.theirCard)
                        ? ctx.bestOutcome
                        : ctx.currentOutcome),

                new(7,  EffectTrigger.WhenEither, OneUP: ctx =>
                    CardExtensions.GetValue(ctx.theirCard) == 9
                        ? ctx.bestOutcome
                        : ctx.currentOutcome),

                // 9 can be played as a 6.
                new(9,  EffectTrigger.WhenEither, OneUP: ctx =>
                    (CardExtensions.GetSuite(ctx.myCard) == CardExtensions.GetSuite(ctx.theirCard))
                        ? ctx.bestOutcome
                        : ctx.currentOutcome),

                new(11, EffectTrigger.WhenEither, OneUP: ctx =>
                    ctx.WarMode && CardExtensions.GetValue(ctx.theirCard) != (int)CardExtensions.SpecialValues.Jack
                        ? ctx.bestOutcome
                        : ctx.currentOutcome),

                new(14, EffectTrigger.WhenEither, OneUP: ctx => ctx.WarMode? ctx.currentOutcome : RoundResult.JokerBurn),
            ];
        }

        public void AddClient(ClientSession clientSession, bool AsPlayer)
        {
            lock (this)
            {
                _Clients.Add(clientSession);
                clientSession.RecieveData += RecieveData;
                clientSession.Closed += RemoveClient;
                if (AsPlayer) 
                {
                    bool spotFound = false;
                    for (int i = 0; i < _Players.Length; i++)
                    {
                        if (_Players[i].client == null) 
                        {
                            spotFound = true;
                            _Players[i].client = clientSession;
                            break;
                        }
                    }

                    if (!spotFound) throw new InvalidOperationException("No more remaining spots");

                    bool allPlayersJoined = true;
                    for (int i = 0; i < _Players.Length; i++)
                    {
                        if (_Players[i].client == null)
                        {
                            allPlayersJoined = false;
                            break;
                        }
                    }

                    if (allPlayersJoined)
                    {
                        cancellationSource.TryReset();
                        State = MatchState.StartingGame;
                        _roundHistory.Clear();
                        _pendingTurns = null;
                        _pendingTopFiveChoice = null;
                        _pendingTopFiveChoicePlayer = null;
                        _pendingTopFiveChoiceCount = 0;
                        _playedCards = [0, 0];
                        Game = Task.Run(Play);
                    }
                }   
            }
        }

        public async Task Play()
        {
            try
            {
                DateTime startingRound = DateTime.UtcNow.AddSeconds(3);
                byte[] beginRoundMessage = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                    type="startingRoundIn",
                    time=startingRound
                }));
                
                Task[] SendTasks;
                lock (this) SendTasks = _Clients.Select(client => 
                    client.webSocket.SendAsync(beginRoundMessage, WebSocketMessageType.Text, true, cancellationSource.Token)
                ).ToArray();

                await Task.WhenAll(SendTasks); 
                if (cancellationSource.IsCancellationRequested) return;
                
                TimeSpan delay = startingRound - DateTime.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay);


                // Begin Round.
                Card[] TotalDeck = CardExtensions.Standard52Deck.ToArray();
                Random.Shared.Shuffle(TotalDeck);


                lock (this)
                {
                    int cards = TotalDeck.Length/_Players.Length;
                    for (int i = 0; i < _Players.Length; i++)
                    {
                        _Players[i].hand = new Hand();
                        _Players[i].hand.AddToDeck(TotalDeck.Skip(i * cards).Take(cards));
                        _Players[i].hand.FillHand();
                        _forceDeckPlayNextTurn[i] = false;
                    }

                    State = MatchState.RunningGame;
                    Turn = 0;
                    _burnedCards.Clear();
                }
                
                
                while (!cancellationSource.IsCancellationRequested)
                {
                    int outcome = EvaluateImmediateOutcome();
                    if (outcome >= 0)
                    {
                        await EndGame(outcome, "deck-exhausted");
                        break;
                    }

                    Interlocked.Increment(ref Turn);
                    InjectRandomJokersForTurn(Turn);
                    lock (_sync)
                    {
                        _pendingTurns =
                        [
                            new(TaskCreationOptions.RunContinuationsAsynchronously),
                            new(TaskCreationOptions.RunContinuationsAsynchronously)
                        ];
                    }

                    await SendHandStates();
                    int? firstPlayer;
                    lock (_sync)
                    {
                        firstPlayer = _firstPlayerNextTurn;
                        _firstPlayerThisTurn = firstPlayer;
                        _firstPlayerNextTurn = null;
                    }
                    await Broadcast(new {
                        type = "requestTurn",
                        turn = Turn,
                        firstPlayer,
                    });

                    Card[] played = await WaitForTurns();
                    var ctx = new RoundContext
                    {
                        Turn = Turn,
                        Played = played,
                        Pile = [played[0], played[1]],
                        Events = [],
                        AppliedEvents = [],
                    };

                    RoundResult roundWinner = ResolveBattle(played[0], played[1], ctx.Pile);
                    ctx.OutcomeCode = (int)roundWinner;
                    if (roundWinner == RoundResult.WarTie)
                    {
                        FinalizeRoundContext(ctx);
                        await EndGame(-1, "war-tie");
                        break;
                    }

                    if (roundWinner == RoundResult.JokerBurn)
                    {
                        BurnCards(ctx.Pile);
                        FinalizeRoundContext(ctx);
                        await Broadcast(new {
                            type = "jokerBurn",
                            turn = Turn,
                            burned = ctx.Pile.Select(c => (int)c).ToArray(),
                            remaining = _Players.Select(p => p.hand.Cards.Count + p.hand.Deck.Count).ToArray(),
                            round = ctx,
                            history = GetRoundHistory(),
                        });
                        continue;
                    }

                    ctx.Winner = roundWinner == RoundResult.Player0Capture ? 0 : 1;
                    ctx.EffectsDisabled = CardExtensions.GetValue(played[0]) == 10
                                       || CardExtensions.GetValue(played[1]) == 10;
                    QueueCardEffects(ctx);
                    await ApplyQueuedEffects(ctx);
                    CollectCards(ctx.Winner, ctx.Pile);
                    FinalizeRoundContext(ctx);

                    await Broadcast(new {
                        type = "roundResult",
                        turn = Turn,
                        winner = ctx.Winner,
                        cards = ctx.Pile.Select(c => (int)c).ToArray(),
                        remaining = _Players.Select(p => p.hand.Cards.Count + p.hand.Deck.Count).ToArray(),
                        round = ctx,
                        history = GetRoundHistory(),
                    });
                }
            }
            catch (OperationCanceledException) {}
            finally
            {
                lock (_sync)
                {
                    _pendingTurns = null;
                    State = MatchState.WaitingForPlayers;
                }
            }
        }

        private Task SendHandStates()
        {
            List<Task> sendTasks = new(_Players.Length);
            lock (_sync)
            {
                for (int i = 0; i < _Players.Length; i++)
                {
                    ClientSession? playerClient = _Players[i].client;
                    if (playerClient is null)
                    {
                        continue;
                    }

                    byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                        type="hand",
                        hand=Convert.ToBase64String(_Players[i].hand.Cards.ToArray()),
                        decklen=_Players[i].hand.Deck.Count,
                        turn=Turn,
                        player=i,
                    }));
                    sendTasks.Add(playerClient.webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationSource.Token));
                }
            }

            Task[] SendTasks = sendTasks.ToArray();
            return Task.WhenAll(SendTasks);
        }


        private void RemovePlayer(int i)
        {
            lock (_sync)
            {
                _Players[i].client?.RecieveData -= RecieveData;
                _Players[i].client?.Closed -= RemoveClient;
                _Players[i].client = null;

                if  (State != MatchState.WaitingForPlayers) cancellationSource.Cancel();
            }
        }

        public void RecieveTurn(int player, byte playedCard)
        {
            lock (_sync)
            {
                if (State != MatchState.RunningGame || _pendingTurns is null)
                {
                    return;
                }

                Hand hand = _Players[player].hand;
                bool forceDeckPlay = _forceDeckPlayNextTurn[player];
                if (_firstPlayerThisTurn is int firstPlayer && player != firstPlayer && !_pendingTurns[firstPlayer].Task.IsCompleted)
                {
                    return;
                }

                if (forceDeckPlay)
                {
                    if (!TryDrawCard(player, out Card deckCard))
                    {
                        cancellationSource.Cancel();
                        return;
                    }

                    _forceDeckPlayNextTurn[player] = false;
                    _playedCards[player] = deckCard;
                    _pendingTurns[player].TrySetResult(deckCard);
                    return;
                }

                int index = hand.Cards.IndexOf(playedCard);
                if (index < 0)
                {
                    return;
                }

                hand.Cards.RemoveAt(index);
                hand.FillHand();
                _playedCards[player] = playedCard;
                _pendingTurns[player].TrySetResult(playedCard);
            }
        }

        public void RecieveWarTurn(int player, byte[] playedCards)
        {
            _ = player;
            _ = playedCards;
        }

        public void RecieveData(ClientSession client, Span<byte> data, WebSocketReceiveResult result)
        {
            if (!_Clients.Contains(client)) return;

            if (result.MessageType != WebSocketMessageType.Text || result.Count <= 0)
            {
                return;
            }

            ReadOnlySpan<byte> incoming = data[..result.Count];
            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(incoming);
            }
            catch
            {
                return;
            }

            if (!root.TryGetProperty("type", out JsonElement typeNode) || typeNode.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string? type = typeNode.GetString();
            for (int i = 0; i < _Players.Length; i++)
            {
                if (_Players[i].client == client)
                {
                    if (string.Equals(type, "effectChoice", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_sync)
                        {
                            if (_pendingTopFiveChoice is not null
                                && _pendingTopFiveChoicePlayer == i
                                && root.TryGetProperty("choice", out JsonElement choiceNode)
                                && choiceNode.TryGetInt32(out int choiceIndex)
                                && choiceIndex >= 0
                                && choiceIndex < _pendingTopFiveChoiceCount)
                            {
                                _pendingTopFiveChoice.TrySetResult(choiceIndex);
                            }
                        }
                    }
                    else if (string.Equals(type, "turn", StringComparison.OrdinalIgnoreCase))
                    {
                        if (root.TryGetProperty("card", out JsonElement cardValue) && cardValue.TryGetByte(out byte playedCard))
                        {
                            RecieveTurn(i, playedCard);
                        }
                        else if (root.TryGetProperty("cardIndex", out JsonElement cardIndex) && cardIndex.TryGetInt32(out int index))
                        {
                            lock (_sync)
                            {
                                if (index >= 0 && index < _Players[i].hand.Cards.Count)
                                {
                                    RecieveTurn(i, _Players[i].hand.Cards[index]);
                                }
                            }
                        }
                    }

                    break;
                }
            }
        }

        public void RemoveClient(ClientSession clientSession)
        {
            lock (_sync)
            {
                for (int i = 0; i < _Players.Length; i++)
                {
                    if (_Players[i].client == clientSession)
                    {
                        RemovePlayer(i);
                    }
                } 
                _Clients.Remove(clientSession);
            }
        }

        private async Task<Card[]> WaitForTurns()
        {
            Task<Card>[] waits;
            lock (_sync)
            {
                if (_pendingTurns is null)
                {
                    throw new InvalidOperationException("No pending turns");
                }

                waits = [
                    _pendingTurns[0].Task,
                    _pendingTurns[1].Task,
                ];
            }

            Card[] cards = await Task.WhenAll(waits).WaitAsync(cancellationSource.Token);
            lock (_sync)
            {
                _firstPlayerThisTurn = null;
            }
            return cards;
        }

        private int EvaluateImmediateOutcome()
        {
            lock (_sync)
            {
                bool player0Alive = EnsureCardsAvailable(0);
                bool player1Alive = EnsureCardsAvailable(1);

                if (!player0Alive && !player1Alive)
                {
                    return -1;
                }

                if (!player0Alive)
                {
                    return 1;
                }

                if (!player1Alive)
                {
                    return 0;
                }

                return -2;
            }
        }

        private bool EnsureCardsAvailable(int player)
        {
            Hand hand = _Players[player].hand;
            hand.FillHand();
            return hand.Cards.Count > 0 || hand.Deck.Count > 0;
        }

        private void InjectRandomJokersForTurn(int turn)
        {
            // Increase chance each turn and cap it to avoid guaranteed joker spam.
            double chance = Math.Min(0.20, 0.05 + (turn - 1) * 0.01);
            Card joker = CardExtensions.GetCard(CardExtensions.Suite.Jester, 0);

            lock (_sync)
            {
                for (int i = 0; i < _Players.Length; i++)
                {
                    if (Random.Shared.NextDouble() >= chance)
                    {
                        continue;
                    }

                    List<Card> deck = _Players[i].hand.Deck;
                    int insertAt = Random.Shared.Next(0, deck.Count + 1);
                    deck.Insert(insertAt, joker);
                }
            }
        }

        private RoundResult ResolveBattle(Card first, Card second, List<Card> pile)
        {
            RoundResult winner = DetermineWinner(first, second, warMode: false);
            if (winner != RoundResult.Tie)
            {
                return winner;
            }

            return ResolveWar(pile);
        }

        private RoundResult ResolveWar(List<Card> pile)
        {
            while (true)
            {
                Card[] topCards = new Card[_Players.Length];

                lock (_sync)
                {
                    bool p0CanWar = CanDrawCards(0, 4);
                    bool p1CanWar = CanDrawCards(1, 4);
                    if (!p0CanWar && !p1CanWar)
                    {
                        return RoundResult.WarTie;
                    }

                    if (!p0CanWar)
                    {
                        return RoundResult.Player1Capture;
                    }

                    if (!p1CanWar)
                    {
                        return RoundResult.Player0Capture;
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        TryDrawCard(0, out Card p0Card);
                        TryDrawCard(1, out Card p1Card);
                        pile.Add(p0Card);
                        pile.Add(p1Card);
                        if (i == 3)
                        {
                            topCards[0] = p0Card;
                            topCards[1] = p1Card;
                        }
                    }
                }

                RoundResult winner = DetermineWinner(topCards[0], topCards[1], warMode: true);
                if (winner == RoundResult.Tie)
                {
                    continue;
                }

                return winner;
            }
        }

        private RoundResult DetermineWinner(Card player0Card, Card player1Card, bool warMode)
        {
            int p0 = CardExtensions.GetValue(player0Card);
            int p1 = CardExtensions.GetValue(player1Card);
            CardExtensions.Suite s0 = CardExtensions.GetSuite(player0Card);
            CardExtensions.Suite s1 = CardExtensions.GetSuite(player1Card);

            if (s0 == CardExtensions.Suite.Jester) return RoundResult.JokerBurn;
            if (s1 == CardExtensions.Suite.Jester) return RoundResult.JokerBurn;

            RoundResult result = p0 > p1
                ? RoundResult.Player0Capture
                : p1 > p0
                    ? RoundResult.Player1Capture
                    : RoundResult.Tie;

            OneUPContext oneUPContext = new()
            {
                myCard = player0Card,
                theirCard = player1Card,
                currentOutcome = result,
                bestOutcome = RoundResult.Player0Capture,
                worstOutcome = RoundResult.Player1Capture,
                WarMode = warMode,
            };

            foreach (CardDetails detail in _cardDetails)
            {
                if (detail.OneUP is null)
                {
                    continue;
                }

                if (p0 == detail.CardValue)
                {
                    oneUPContext.myCard = player0Card;
                    oneUPContext.theirCard = player1Card;
                    oneUPContext.currentOutcome = result;
                    oneUPContext.bestOutcome = RoundResult.Player0Capture;
                    oneUPContext.worstOutcome = RoundResult.Player1Capture;
                    result = detail.OneUP(oneUPContext);
                }

                if (p1 == detail.CardValue)
                {
                    oneUPContext.myCard = player1Card;
                    oneUPContext.theirCard = player0Card;
                    oneUPContext.currentOutcome = result;
                    oneUPContext.bestOutcome = RoundResult.Player1Capture;
                    oneUPContext.worstOutcome = RoundResult.Player0Capture;
                    result = detail.OneUP(oneUPContext);
                }
            }

            return result;
        }

        private void QueueCardEffects(RoundContext ctx)
        {
            if (ctx.EffectsDisabled) return;

            foreach (CardDetails detail in _cardDetails)
            {
                if (detail.CreateEvent is null)
                {
                    continue;
                }

                bool applies = detail.Trigger switch
                {
                    EffectTrigger.WhenWinner => ctx.WinnerCardValue == detail.CardValue,
                    EffectTrigger.WhenLoser  => ctx.LoserCardValue  == detail.CardValue,
                    EffectTrigger.WhenEither => ctx.WinnerCardValue == detail.CardValue
                                            || ctx.LoserCardValue  == detail.CardValue,
                    _ => false,
                };

                if (!applies)
                {
                    continue;
                }

                IRoundEvent? @event = detail.CreateEvent(ctx);
                if (@event is not null)
                {
                    ctx.Events.AddLast(@event);
                }
            }
        }

        private async Task ApplyQueuedEffects(RoundContext ctx)
        {
            foreach (IRoundEvent @event in ctx.Events)
            {
                await @event.RecieveUserChoice();
                await @event.ApplyEvent();
                ctx.AppliedEvents.Add(@event.ToSnapshot());
            }
        }

        private void FinalizeRoundContext(RoundContext ctx)
        {
            lock (_sync)
            {
                ctx.RemainingCards =
                [
                    _Players[0].hand.Cards.Count + _Players[0].hand.Deck.Count,
                    _Players[1].hand.Cards.Count + _Players[1].hand.Deck.Count,
                ];
                ctx.BurnedCardsTotal = _burnedCards.Count;
                _roundHistory.Add(ctx);
            }
        }

        public IReadOnlyList<RoundContext> GetRoundHistory()
        {
            lock (_sync)
            {
                return _roundHistory.ToArray();
            }
        }

        private void CollectCards(int winner, List<Card> pile)
        {
            lock (_sync)
            {
                _Players[winner].hand.AddToDeck(pile);
                _Players[winner].hand.FillHand();
                _Players[1 - winner].hand.FillHand();
            }
        }

        private void BurnCards(List<Card> cards)
        {
            lock (_sync)
            {
                _burnedCards.AddRange(cards);
            }
        }

        private bool CanDrawCards(int player, int count)
        {
            Hand hand = _Players[player].hand;
            int available = hand.Cards.Count + hand.Deck.Count;
            return available >= count;
        }

        private bool TryDrawCard(int player, out Card card)
        {
            Hand hand = _Players[player].hand;
            if (hand.Cards.Count == 0)
            {
                hand.FillHand();
            }

            if (hand.Cards.Count > 0)
            {
                card = hand.Cards[0];
                hand.Cards.RemoveAt(0);
                return true;
            }

            if (hand.Deck.Count > 0)
            {
                card = hand.Deck[0];
                hand.Deck.RemoveAt(0);
                return true;
            }

            card = 0;
            return false;
        }

        private Task Broadcast(object payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            Task[] sendTasks;
            lock (_sync)
            {
                sendTasks = _Clients
                    .Select(c => c.webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationSource.Token))
                    .ToArray();
            }

            return Task.WhenAll(sendTasks);
        }

        private Task SendToPlayer(int player, object payload)
        {
            ClientSession? client;
            lock (_sync)
            {
                client = _Players[player].client;
            }

            if (client is null)
            {
                return Task.CompletedTask;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            return client.webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationSource.Token);
        }

        private async Task EndGame(int winner, string reason)
        {
            await Broadcast(new {
                type = "matchEnded",
                winner,
                reason,
            });

            cancellationSource.Cancel();
        }

        private readonly (ClientSession? client, Hand hand)[] _Players;
        private readonly List<ClientSession> _Clients;
        private readonly CancellationTokenSource cancellationSource;
        private readonly bool[] _forceDeckPlayNextTurn;
        private int? _firstPlayerNextTurn;
        private int? _firstPlayerThisTurn;
        private readonly object _sync = new();
        private TaskCompletionSource<Card>[]? _pendingTurns;
        private TaskCompletionSource<int>? _pendingTopFiveChoice;
        private int? _pendingTopFiveChoicePlayer;
        private int _pendingTopFiveChoiceCount;
        private Card[] _playedCards;
        private readonly List<Card> _burnedCards = [];
        private readonly List<RoundContext> _roundHistory = [];
        private readonly CardDetails[] _cardDetails;


        private enum RoundResult 
        {
            WarTie = -2,
            JokerBurn = -3,
            Tie = -1,
            Player0Capture = 0,
            Player1Capture = 1,
        }

        // ---- Round context --------------------------------------------------
        // Carries all relevant state for a single round; passed to every effect.

        [Serializable]
        public sealed class RoundContext
        {
            [JsonInclude] public required int Turn;
            [JsonInclude] public required Card[] Played;      // indexed by player index
            [JsonInclude] public required List<Card> Pile;

            [JsonIgnore]
            internal LinkedList<IRoundEvent> Events = [];

            [JsonInclude] public required List<RoundEventSnapshot> AppliedEvents;
            [JsonInclude] public bool EffectsDisabled;
            [JsonInclude] public int OutcomeCode;
            [JsonInclude] public int Winner;
            public int Loser => 1 - Winner;
            public int WinnerCardValue => CardExtensions.GetValue(Played[Winner]);
            public int LoserCardValue  => CardExtensions.GetValue(Played[Loser]);
            [JsonInclude] public int[] RemainingCards = [0, 0];
            [JsonInclude] public int BurnedCardsTotal;
        }

        [Serializable]
        public sealed class RoundEventSnapshot
        {
            [JsonInclude] public required string EventType;
            [JsonInclude] public required string Description;
            [JsonInclude] public int? SourcePlayer;
            [JsonInclude] public int[] TargetPlayers = [];
            [JsonInclude] public Dictionary<string, string> Data = [];
        }

        // ---- Post-round effect table ----------------------------------------
        // To add an effect: add one PostRoundEffect entry in the constructor.
        // To remove an effect: delete its entry from the constructor.

        private enum EffectTrigger { WhenWinner, WhenLoser, WhenEither }

        private readonly record struct CardDetails 
        (            
            int CardValue,
            EffectTrigger Trigger,
            Func<RoundContext, IRoundEvent?>? CreateEvent = null,
            Func<OneUPContext, RoundResult>? OneUP = null
        );

        private struct OneUPContext
        {
            public Card myCard;
            public Card theirCard;
            public RoundResult currentOutcome;
            public RoundResult bestOutcome;
            public RoundResult worstOutcome;
            public bool WarMode;
        }

        internal interface IRoundEvent
        {
            public Task RecieveUserChoice();
            public string DescribeEvent();
            public Task ApplyEvent();
            public RoundEventSnapshot ToSnapshot();
            
        }
    }
}