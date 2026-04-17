using System.Collections.Specialized;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        public Guid Id { get; private set;}

        public int ConnectedPlayerCount
        {
            get
            {
                lock (this)
                {
                    int count = 0;
                    for (int i = 0; i < _Players.Length; i++)
                        if (_Players[i].client != null) count++;
                    return count;
                }
            }
        }

        public Match(Guid Id)
        {
            this.Id = Id;
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
                        : null),

                new(7,  EffectTrigger.WhenEither, OneUP: ctx =>
                    CardExtensions.GetValue(ctx.theirCard) == 9
                        ? ctx.bestOutcome
                        : null),

                // 9 can be played as a 6.
                new(9,  EffectTrigger.WhenEither, OneUP: ctx =>
                    (CardExtensions.GetSuite(ctx.myCard) == CardExtensions.GetSuite(ctx.theirCard))
                        ? ctx.bestOutcome
                        : null),

                new(11, EffectTrigger.WhenEither, OneUP: ctx =>
                    ctx.WarMode && CardExtensions.GetValue(ctx.theirCard) != (int)CardExtensions.SpecialValues.Jack
                        ? ctx.bestOutcome
                        : null),

                new(14, EffectTrigger.WhenEither, OneUP: ctx => ctx.bestOutcome),
            ];
        }

        public int? AddClient(ClientSession clientSession, bool AsPlayer)
        {
            int? playerIndex = null;
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
                            playerIndex = i;
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

            return playerIndex;
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
                Array.Resize(ref TotalDeck, 25);

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
                    RoundResult? outcome = EvaluateImmediateOutcome();
                    if (outcome != null)
                    {
                        await EndGame(outcome.Value, "deck-exhausted");
                        break;
                    }

                    Interlocked.Increment(ref Turn);
                    List<RoundEventSnapshot> appliedEvents = [];
                    if (cancellationSource.IsCancellationRequested)
                    {
                        break;
                    }
                    

                    lock (this)
                    {
                        _pendingTurns =
                        [
                            new(TaskCreationOptions.RunContinuationsAsynchronously),
                            new(TaskCreationOptions.RunContinuationsAsynchronously)
                        ];
                    }

                    await SendHandStates();
                    int? firstPlayer;
                    lock (this)
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

                    // Resolve forced-deck turns immediately so clients with empty hands cannot softlock the turn.
                    await AutoResolveForcedDeckTurns();

                    Card[] played = await WaitForTurns();
                    var ctx = new RoundContext
                    {
                        Turn = Turn,
                        Played = played,
                        Pile = [played[0], played[1]],
                        Events = [],
                        AppliedEvents = appliedEvents,
                    };

                    RoundResult roundWinner = ResolveBattle(played[0], played[1], ctx.Pile);
                    int preRoundWinner = roundWinner switch
                    {
                        RoundResult.Player0Capture => 0,
                        RoundResult.Player1Capture => 1,
                        _ => -1,
                    };
                    await Broadcast(new {
                        type = "preroundresult",
                        played = played.Select(x => (int)x).ToArray(),
                        turn = Turn,
                        winner = preRoundWinner,
                    });

                    ctx.OutcomeCode = (int)roundWinner;
                    if (roundWinner == RoundResult.Tie)
                    {
                        (roundWinner, Card[] warPlayed) = await ResolveWar(ctx.Pile);
                        if (cancellationSource.Token.IsCancellationRequested) break;
                        ctx.Played = warPlayed;
                        ctx.OutcomeCode = (int)roundWinner;
                    }

                    AddTurnEvents(ctx);
                    if (roundWinner == RoundResult.JokerBurn)
                    {
                        ctx.Winner = null;
                        BurnCards(ctx.Pile);
                    }
                    else if (roundWinner == RoundResult.WarTie)
                    {
                        ctx.Winner = null;
                    }
                    else
                    {
                        ctx.Winner = roundWinner == RoundResult.Player0Capture ? 0 : 1;
                        ctx.EffectsDisabled = CardExtensions.GetValue(played[0]) == 10
                                        || CardExtensions.GetValue(played[1]) == 10;
                    }

                    QueueCardEffects(ctx);
                    await ApplyQueuedEffects(ctx);
                    if (ctx.Winner is int winner)
                    {
                        CollectCards(winner, ctx.Pile);
                    }
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

                    if (roundWinner == RoundResult.WarTie) 
                    {
                        await EndGame(RoundResult.WarTie, "war-tie");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) {}
            catch (Exception except)
            {
                Console.WriteLine(except);
            }
            finally
            {
                lock (this)
                {
                    _pendingTurns = null;
                    State = MatchState.WaitingForPlayers;
                }
            }
        }

        private Task SendHandStates()
        {
            List<Task> sendTasks = new(_Players.Length);
            lock (this)
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
                        handlen=_Players[i].hand.Cards.Count,
                        opponentHandLen=_Players[1 - i].hand.Cards.Count,
                        decklen=_Players[i].hand.Deck.Count,
                        opponentDeckLen=_Players[1 - i].hand.Cards.Count,
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
            lock (this)
            {
                _Players[i].client?.RecieveData -= RecieveData;
                _Players[i].client?.Closed -= RemoveClient;
                _Players[i].client = null;

                if  (State != MatchState.WaitingForPlayers) 
                {
                    EndGame(RoundResult.Tie, "player-disconnect").Wait();
                }
            }
        }

        public async Task RecieveTurn(int player, byte playedCard)
        {
            bool accepted = false;
            bool revealCard = false;
            Card acceptedCard = 0;
            int turn = 0;

            lock (this)
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
                    accepted = true;
                    acceptedCard = deckCard;
                    revealCard = _firstPlayerThisTurn == player;
                    turn = Turn;
                }
                else
                {
                    int index = hand.Cards.IndexOf(playedCard);
                    if (index < 0)
                    {
                        return;
                    }

                    hand.Cards.RemoveAt(index);
                    hand.FillHand();
                    accepted = true;
                    acceptedCard = playedCard;
                    revealCard = _firstPlayerThisTurn == player;
                    turn = Turn;
                }
            }

            if (!accepted)
            {
                return;
            }

            await SendToPlayer(1 - player, new {
                type = "opponentPlayed",
                turn,
                player,
                card = revealCard ? (int?)acceptedCard : null
            });

            lock (this)
            {
                if (State != MatchState.RunningGame || _pendingTurns is null)
                {
                    return;
                }

                _playedCards[player] = acceptedCard;
                _pendingTurns[player].TrySetResult(acceptedCard);
            }
        }

        public void RecieveWarTurn(int player, byte[] selectedCards)
        {
            List<object> progressNotifications = [];
            lock (this)
            {
                if (_pendingWarTurns is null || _pendingWarTurns[player].Task.IsCompleted)
                    return;

                if (_pendingWarSelections is null || _pendingWarSelectionTargets is null)
                    return;

                Hand hand = _Players[player].hand;
                List<Card> selected = _pendingWarSelections[player];
                int targetSelections = _pendingWarSelectionTargets[player];

                foreach (byte card in selectedCards)
                {
                    if (selected.Count >= targetSelections)
                    {
                        break;
                    }

                    int idx = hand.Cards.IndexOf(card);
                    if (idx < 0) return;

                    hand.Cards.RemoveAt(idx);
                    selected.Add(card);

                    // Sacrifices (not the final war card) are revealed immediately.
                    // The war card (last hand selection) stays hidden until warReveal.
                    bool isSacrifice = selected.Count < targetSelections;
                    progressNotifications.Add(new
                    {
                        type = "warProgress",
                        turn = Turn,
                        player,
                        slot = selected.Count - 1,
                        count = selected.Count,
                        required = targetSelections,
                        card = isSacrifice ? (int?)card : null,
                    });
                }

                if (selected.Count < targetSelections)
                {
                    // Not enough user selections yet; keep waiting for more partial submissions.
                    goto SendProgress;
                }

                List<Card> chosen = [.. selected];
                int remaining = 4 - chosen.Count;
                for (int i = 0; i < remaining; i++)
                {
                    if (!TryDrawCard(player, out Card deckCard)) return;
                    int insertAt = chosen.Count > 0 ? chosen.Count - 1 : chosen.Count;
                    chosen.Insert(insertAt, deckCard);
                }

                if (chosen.Count != 4) return;

                _pendingWarTurns[player].TrySetResult([.. chosen]);
            }

        SendProgress:
            foreach (object payload in progressNotifications)
            {
                Broadcast(payload);
            }
        }

        public async Task RecieveData(ClientSession client, ReadOnlyMemory<byte> data, WebSocketReceiveResult result)
        {
            if (!_Clients.Contains(client)) return;

            if (result.MessageType != WebSocketMessageType.Text || result.Count <= 0)
            {
                return;
            }

            ReadOnlySpan<byte> incoming = data.Span[..result.Count];
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
            int? playerIndex = null;
            for (int i = 0; i < _Players.Length; i++)
            {
                if (_Players[i].client == client)
                {
                    playerIndex = i;
                    break;
                }
            }

            if (string.Equals(type, "meta", StringComparison.OrdinalIgnoreCase))
            {
                JsonElement metadata = root.TryGetProperty("data", out JsonElement metadataNode)
                    ? metadataNode.Clone()
                    : root.Clone();
                await Broadcast(new {
                    type = "meta",
                    from = new {
                        player = playerIndex,
                        spectate = !playerIndex.HasValue,
                    },
                    data = metadata,
                });
                return;
            }

            if (!playerIndex.HasValue)
            {
                return;
            }

            int player = playerIndex.Value;
            if (string.Equals(type, "effectChoice", StringComparison.OrdinalIgnoreCase))
            {
                lock (this)
                {
                    if (_pendingTopFiveChoice is not null
                        && _pendingTopFiveChoicePlayer == player
                        && root.TryGetProperty("choice", out JsonElement choiceNode)
                        && choiceNode.TryGetInt32(out int choiceIndex)
                        && choiceIndex >= 0
                        && choiceIndex < _pendingTopFiveChoiceCount)
                    {
                        _pendingTopFiveChoice.TrySetResult(choiceIndex);
                    }
                }
            }
            else if (string.Equals(type, "warTurn", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("cards", out JsonElement warCardsNode)
                    && warCardsNode.ValueKind == JsonValueKind.Array)
                {
                    List<byte> cards = [];
                    foreach (JsonElement el in warCardsNode.EnumerateArray())
                    {
                        if (el.TryGetByte(out byte c)) cards.Add(c);
                    }
                    RecieveWarTurn(player, [.. cards]);
                }
            }
            else if (string.Equals(type, "turn", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("card", out JsonElement cardValue) && cardValue.TryGetByte(out byte playedCard))
                {
                    await RecieveTurn(player, playedCard);
                }
                else if (root.TryGetProperty("cardIndex", out JsonElement cardIndex) && cardIndex.TryGetInt32(out int index))
                {
                    byte selectedCard;
                    lock (this)
                    {
                        if (index < 0 || index >= _Players[player].hand.Cards.Count)
                        {
                            return;
                        }

                        selectedCard = _Players[player].hand.Cards[index];
                    }

                    await RecieveTurn(player, selectedCard);
                }
            }
        }

        public void RemoveClient(ClientSession clientSession)
        {
            lock (this)
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
            lock (this)
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
            lock (this)
            {
                _firstPlayerThisTurn = null;
            }
            return cards;
        }

        private async Task AutoResolveForcedDeckTurns()
        {
            int[] order;
            lock (this)
            {
                if (State != MatchState.RunningGame || _pendingTurns is null)
                {
                    return;
                }

                if (_firstPlayerThisTurn is int firstPlayer)
                {
                    order = [firstPlayer, 1 - firstPlayer];
                }
                else
                {
                    order = [0, 1];
                }
            }

            foreach (int player in order)
            {
                bool shouldAutoPlay;
                lock (this)
                {
                    shouldAutoPlay = State == MatchState.RunningGame
                        && _pendingTurns is not null
                        && _forceDeckPlayNextTurn[player]
                        && !_pendingTurns[player].Task.IsCompleted;
                }

                if (shouldAutoPlay)
                {
                    await RecieveTurn(player, 0);
                }
            }
        }

        private RoundResult? EvaluateImmediateOutcome()
        {
            lock (this)
            {
                bool player0Alive = EnsureCardsAvailable(0);
                bool player1Alive = EnsureCardsAvailable(1);
                if (player0Alive && player1Alive) return null;

                if (!player0Alive && !player1Alive)
                {
                    return RoundResult.Tie;
                }
                
                return player0Alive? RoundResult.Player0Capture : RoundResult.Player1Capture;
            }    
        }

        private bool EnsureCardsAvailable(int player)
        {
            Hand hand = _Players[player].hand;
            hand.FillHand();
            return hand.Cards.Count > 0 || hand.Deck.Count > 0;
        }

        private void AddTurnEvents(RoundContext ctx)
        {
            if (ctx.Turn > 1)
            {
                ctx.Events.AddLast(new RandomJokerInjectionEvent(this, ctx));
            }
        }

        private RoundResult ResolveBattle(Card first, Card second, List<Card> pile)
        {
            RoundResult winner = DetermineWinner(first, second, warMode: false);
            return winner;
        }

        private async Task<(RoundResult Result, Card[] Played)> ResolveWar(List<Card> pile)
        {
            while (true)
            {
                bool p0CanWar = CanDrawCards(0, 4);
                bool p1CanWar = CanDrawCards(1, 4);
                for (int i = 0; i < _Players.Length; i++)
                {
                    _Players[i].hand.FillHand();
                }

                if (!p0CanWar && !p1CanWar) return (RoundResult.WarTie, [0, 0]);

                if (!p0CanWar) 
                {
                    await EndGame(RoundResult.Player1Capture, "deck-exhausted");
                    return (RoundResult.Player1Capture, [0, 0]);
                }

                if (!p1CanWar) 
                {
                    await EndGame(RoundResult.Player0Capture, "deck-exhausted");
                    return (RoundResult.Player0Capture, [0, 0]);
                }

                lock (this)
                {
                    _pendingWarTurns =
                    [
                        new(TaskCreationOptions.RunContinuationsAsynchronously),
                        new(TaskCreationOptions.RunContinuationsAsynchronously),
                    ];

                    _pendingWarSelections =
                    [
                        [],
                        [],
                    ];

                    _pendingWarSelectionTargets =
                    [
                        Math.Min(4, _Players[0].hand.Cards.Count),
                        Math.Min(4, _Players[1].hand.Cards.Count),
                    ];

                    for (int player = 0; player < 2; player++)
                    {
                        if (_pendingWarSelectionTargets[player] == 0)
                        {
                            RecieveWarTurn(player, []);
                        }
                    }
                }

                await SendHandStates();
                await Broadcast(new { type = "requestWarTurn", turn = Turn });

                Task<Card[]>[] waits;
                lock (this)
                {
                    waits = [_pendingWarTurns[0].Task, _pendingWarTurns[1].Task];
                }

                Card[][] selections = await Task.WhenAll(waits).WaitAsync(cancellationSource.Token);

                lock (this)
                {
                    _pendingWarTurns = null;
                    _pendingWarSelections = null;
                    _pendingWarSelectionTargets = null;
                }

                // selections[player] = [sacrifice0, sacrifice1, sacrifice2, warCard]
                Card[] warCards = new Card[2];
                for (int player = 0; player < 2; player++)
                {
                    for (int i = 0; i < 3; i++) pile.Add(selections[player][i]);
                    warCards[player] = selections[player][3];
                    pile.Add(warCards[player]);
                }

                // Both players have finished — reveal both war cards simultaneously.
                await Broadcast(new {
                    type = "warReveal",
                    turn = Turn,
                    cards = warCards.Select(c => (int)c).ToArray(),
                });

                RoundResult result = DetermineWinner(warCards[0], warCards[1], warMode: true);

                if (result == RoundResult.JokerBurn) return (RoundResult.JokerBurn, warCards);
                if (result != RoundResult.Tie) return (result, warCards);

                // Another tie — loop for another war round
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
                valueOutcome = result,
                bestOutcome = RoundResult.Player0Capture,
                worstOutcome = RoundResult.Player1Capture,
                WarMode = warMode,
            };

            RoundResult? P0oneUP = null;
            RoundResult? P1oneUP = null;
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
                    oneUPContext.valueOutcome = result;
                    oneUPContext.bestOutcome = RoundResult.Player0Capture;
                    oneUPContext.worstOutcome = RoundResult.Player1Capture;
                    P0oneUP = detail.OneUP(oneUPContext) ?? P0oneUP;
                }

                if (p1 == detail.CardValue)
                {
                    oneUPContext.myCard = player1Card;
                    oneUPContext.theirCard = player0Card;
                    oneUPContext.valueOutcome = result;
                    oneUPContext.bestOutcome = RoundResult.Player1Capture;
                    oneUPContext.worstOutcome = RoundResult.Player0Capture;
                    P1oneUP = detail.OneUP(oneUPContext) ?? P0oneUP;
                }
            }

            if (P0oneUP != P1oneUP && P0oneUP != null && P1oneUP != null) return RoundResult.Tie;
            return P0oneUP ?? P1oneUP ?? result;
        }

        private void QueueCardEffects(RoundContext ctx)
        {
            if (ctx.EffectsDisabled) return;

            int p0CardValue = CardExtensions.GetValue(ctx.Played[0]);
            int p1CardValue = CardExtensions.GetValue(ctx.Played[1]);
            int aceValue = (int)CardExtensions.SpecialValues.Ace;
            int? opposingCardPlayerForAce = p0CardValue == aceValue && p1CardValue != aceValue
                ? 1
                : p1CardValue == aceValue && p0CardValue != aceValue
                    ? 0
                    : null;

            foreach (CardDetails detail in _cardDetails)
            {
                if (detail.CreateEvent is null)
                {
                    continue;
                }

                bool applies = detail.Trigger switch
                {
                    EffectTrigger.WhenWinner => ctx.WinnerCardValue is int winnerValue && winnerValue == detail.CardValue,
                    EffectTrigger.WhenLoser  => ctx.LoserCardValue  is int loserValue && loserValue == detail.CardValue,
                    EffectTrigger.WhenEither => (ctx.WinnerCardValue is int eitherWinnerValue && eitherWinnerValue == detail.CardValue)
                                            || (ctx.LoserCardValue  is int eitherLoserValue  && eitherLoserValue == detail.CardValue),
                    _ => false,
                };

                bool forcedByAceRule = !applies
                    && opposingCardPlayerForAce is int opposingCardPlayer
                    && CardExtensions.GetValue(ctx.Played[opposingCardPlayer]) == detail.CardValue;

                if (!applies && !forcedByAceRule)
                {
                    continue;
                }

                RoundContext effectCtx = ctx;
                if (forcedByAceRule && opposingCardPlayerForAce is int forcedPlayer)
                {
                    int forcedWinner = detail.Trigger switch
                    {
                        EffectTrigger.WhenWinner => forcedPlayer,
                        EffectTrigger.WhenLoser => 1 - forcedPlayer,
                        _ => ctx.Winner ?? forcedPlayer,
                    };

                    effectCtx = CloneRoundContextWithWinner(ctx, forcedWinner);
                }

                IRoundEvent? @event = detail.CreateEvent(effectCtx);
                if (@event is not null)
                {
                    ctx.Events.AddLast(@event);
                }
            }
        }

        private static RoundContext CloneRoundContextWithWinner(RoundContext source, int winner)
        {
            return new RoundContext
            {
                Turn = source.Turn,
                Played = source.Played,
                Pile = source.Pile,
                Events = [],
                AppliedEvents = source.AppliedEvents,
                EffectsDisabled = source.EffectsDisabled,
                OutcomeCode = source.OutcomeCode,
                Winner = winner,
                RemainingCards = source.RemainingCards,
                BurnedCardsTotal = source.BurnedCardsTotal,
            };
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
            lock (this)
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
            lock (this)
            {
                return _roundHistory.ToArray();
            }
        }

        private void CollectCards(int winner, List<Card> pile)
        {
            lock (this)
            {
                _Players[winner].hand.AddToDeck(pile);
                _Players[winner].hand.FillHand();
                _Players[1 - winner].hand.FillHand();
            }
        }

        private void BurnCards(List<Card> cards)
        {
            lock (this)
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
            lock (this)
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
            lock (this)
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

        private async Task EndGame(RoundResult winner, string reason)
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
        private TaskCompletionSource<Card>[]? _pendingTurns;
        private TaskCompletionSource<int>? _pendingTopFiveChoice;
        private int? _pendingTopFiveChoicePlayer;
        private int _pendingTopFiveChoiceCount;
        private Card[] _playedCards;
        private readonly List<Card> _burnedCards = [];
        private readonly List<RoundContext> _roundHistory = [];
        private readonly CardDetails[] _cardDetails;
        private TaskCompletionSource<Card[]>[]? _pendingWarTurns;
        private List<Card>[]? _pendingWarSelections;
        private int[]? _pendingWarSelectionTargets;


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
            [JsonInclude] public int? Winner;
            public int? Loser => Winner.HasValue ? 1 - Winner.Value : null;
            public int? WinnerCardValue => Winner.HasValue ? CardExtensions.GetValue(Played[Winner.Value]) : null;
            public int? LoserCardValue  => Loser.HasValue ? CardExtensions.GetValue(Played[Loser.Value]) : null;
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
            [JsonInclude] public NameValueCollection Data = [];
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
            Func<OneUPContext, RoundResult?>? OneUP = null
        );

        private struct OneUPContext
        {
            public Card myCard;
            public Card theirCard;
            public RoundResult valueOutcome;
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