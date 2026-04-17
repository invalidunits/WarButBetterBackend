namespace WarButBetterBackend
{
    using Card = byte;

    public partial class Match
    {
        private sealed class RandomJokerInjectionEvent : IRoundEvent
        {
            private readonly Match _match;
            private readonly RoundContext _context;
            private readonly List<int> _players = [];
            private readonly List<int> _insertIndexes = [];

            public RandomJokerInjectionEvent(Match match, RoundContext context)
            {
                _match = match;
                _context = context;
            }

            public bool InjectedAny => _players.Count > 0;

            public Task RecieveUserChoice() => Task.CompletedTask;

            public string DescribeEvent()
            {
                if (!InjectedAny) return "";
                return $"A distant laugh echos from beyond.";
            }

            public Task ApplyEvent()
            {
                Card joker = CardExtensions.GetCard(CardExtensions.Suite.Jester, 0);
                double chance = Math.Min(0.20, 0.05 + (_context.Turn - 1) * 0.01);
                lock (_match)
                {
                    for (int i = 0; i < _match._Players.Length; i++)
                    {
                        if (Random.Shared.NextDouble() >= chance) continue;

                        List<Card> deck = _match._Players[i].hand.Deck;
                        int insertAt = Random.Shared.Next(0, deck.Count + 1);
                        deck.Insert(insertAt, joker);
                        _players.Add(i);
                        _insertIndexes.Add(insertAt);
                    }
                }

                return Task.CompletedTask;
            }

            public RoundEventSnapshot ToSnapshot() => new()
            {
                EventType = "turn_start_joker_injection",
                Description = DescribeEvent(),
                SourcePlayer = null,
                TargetPlayers = _players.ToArray(),
                Data = new()
                {
                    ["insertIndexes"] = string.Join(",", _insertIndexes),
                }
            };
        }

        private sealed class ForceWinnerFirstEvent : IRoundEvent
        {
            private readonly Match _match;
            private readonly int _winner;

            public ForceWinnerFirstEvent(Match match, RoundContext ctx)
            {
                _match = match;
                _winner = ctx.Winner ?? throw new InvalidOperationException("ForceWinnerFirstEvent requires a winner.");
            }

            public Task RecieveUserChoice() => Task.CompletedTask;

            public string DescribeEvent() => $"Player {_winner} is forced to play first on the next turn.";

            public Task ApplyEvent()
            {
                lock (_match)
                {
                    _match._firstPlayerNextTurn = _winner;
                }

                return Task.CompletedTask;
            }

            public RoundEventSnapshot ToSnapshot() => new()
            {
                EventType = "two_force_first",
                Description = DescribeEvent(),
                SourcePlayer = _winner,
                TargetPlayers = [_winner],
            };
        }

        private sealed class ChooseFromTopFiveEvent : IRoundEvent
        {
            private readonly Match _match;
            private readonly int _player;
            private int _selectedIndex;
            private Card[] _presentedCards = [];
            private Card? _chosenCard;

            public ChooseFromTopFiveEvent(Match match, RoundContext ctx)
            {
                _match = match;
                _player = ctx.Loser ?? throw new InvalidOperationException("ChooseFromTopFiveEvent requires a loser.");
                _selectedIndex = 0;
            }

            public string DescribeEvent()
            {
                string chosen = _chosenCard.HasValue ? ((int)_chosenCard.Value).ToString() : "none";
                return $"Player {_player} tooke card {chosen} from the top 5 of their deck.";
            }

            public async Task RecieveUserChoice()
            {
                Card[] topCards;
                TaskCompletionSource<int>? pendingChoice = null;

                lock (_match)
                {
                    Hand hand = _match._Players[_player].hand;
                    int count = Math.Min(5, hand.Deck.Count);
                    if (count <= 0)
                    {
                        return;
                    }

                    topCards = hand.Deck.Take(count).ToArray();
                    _presentedCards = topCards;
                    if (count == 1)
                    {
                        _selectedIndex = 0;
                        return;
                    }

                    pendingChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _match._pendingTopFiveChoice = pendingChoice;
                    _match._pendingTopFiveChoicePlayer = _player;
                    _match._pendingTopFiveChoiceCount = count;
                }

                await _match.SendToPlayer(_player, new {
                    type = "chooseTopFive",
                    turn = _match.Turn,
                    cards = topCards.Select(c => (int)c).ToArray(),
                });

                try
                {
                    _selectedIndex = await pendingChoice!.Task.WaitAsync(TimeSpan.FromSeconds(20), _match.cancellationSource.Token);
                }
                catch
                {
                    _selectedIndex = Random.Shared.Next(0, topCards.Length);
                }
                finally
                {
                    lock (_match)
                    {
                        if (ReferenceEquals(_match._pendingTopFiveChoice, pendingChoice))
                        {
                            _match._pendingTopFiveChoice = null;
                            _match._pendingTopFiveChoicePlayer = null;
                            _match._pendingTopFiveChoiceCount = 0;
                        }
                    }
                }
            }

            public Task ApplyEvent()
            {
                lock (_match)
                {
                    Hand hand = _match._Players[_player].hand;
                    int count = Math.Min(5, hand.Deck.Count);
                    if (count <= 0)
                    {
                        return Task.CompletedTask;
                    }

                    List<Card> top = hand.Deck.Take(count).ToList();
                    hand.Deck.RemoveRange(0, count);
                    int pick = Math.Clamp(_selectedIndex, 0, top.Count - 1);
                    Card chosen = top[pick];
                    _chosenCard = chosen;
                    top.RemoveAt(pick);

                    hand.Cards.Add(chosen);
                    hand.Deck.InsertRange(0, top);
                }

                return Task.CompletedTask;
            }

            public RoundEventSnapshot ToSnapshot() => new()
            {
                EventType = "three_choose_top_five",
                Description = DescribeEvent(),
                SourcePlayer = _player,
                TargetPlayers = [_player],
                Data = new ()
                {
                    ["selectedIndex"] = _selectedIndex.ToString(),
                    ["chosenCard"] = _chosenCard?.ToString() ?? string.Empty,
                    ["presentedCards"] = string.Join(",", _presentedCards.Select(c => ((int)c).ToString())),
                }
            };
        }

        private sealed class ShuffleOpponentDeckEvent : IRoundEvent
        {
            private readonly Match _match;
            private readonly int _opponent;

            public ShuffleOpponentDeckEvent(Match match, RoundContext ctx)
            {
                _match = match;
                _opponent = ctx.Loser ?? throw new InvalidOperationException("ShuffleOpponentDeckEvent requires a loser.");
            }

            public Task RecieveUserChoice() => Task.CompletedTask;

            public string DescribeEvent() => $"Player {_opponent}'s deck was shuffled.";

            public Task ApplyEvent()
            {
                lock (_match)
                {
                    List<Card> opponentDeck = _match._Players[_opponent].hand.Deck;
                    Card[] shuffled = opponentDeck.ToArray();
                    Random.Shared.Shuffle(shuffled);
                    opponentDeck.Clear();
                    opponentDeck.AddRange(shuffled);
                }

                return Task.CompletedTask;
            }

            public RoundEventSnapshot ToSnapshot() => new()
            {
                EventType = "four_shuffle_opponent_deck",
                Description = DescribeEvent(),
                SourcePlayer = null,
                TargetPlayers = [_opponent],
            };
        }

        private sealed class SwapHandCardEvent : IRoundEvent
        {
            private readonly Match _match;
            private readonly int _winner;
            private readonly int _loser;
            private int? _winnerIndex;
            private int? _loserIndex;

            public SwapHandCardEvent(Match match, RoundContext ctx)
            {
                _match = match;
                _winner = ctx.Winner ?? throw new InvalidOperationException("SwapHandCardEvent requires a winner.");
                _loser = ctx.Loser ?? throw new InvalidOperationException("SwapHandCardEvent requires a loser.");
            }

            public Task RecieveUserChoice() => Task.CompletedTask;

            public string DescribeEvent()
            {
                string wi = _winnerIndex?.ToString() ?? "n/a";
                string li = _loserIndex?.ToString() ?? "n/a";
                return $"Player {_loser} lost with 5 and swapped hand card {li} with player {_winner}'s hand card {wi}.";
            }

            public Task ApplyEvent()
            {
                lock (_match)
                {
                    List<Card> winnerHand = _match._Players[_winner].hand.Cards;
                    List<Card> loserHand = _match._Players[_loser].hand.Cards;
                    if (winnerHand.Count > 0 && loserHand.Count > 0)
                    {
                        int wi = Random.Shared.Next(0, winnerHand.Count);
                        int li = Random.Shared.Next(0, loserHand.Count);
                        _winnerIndex = wi;
                        _loserIndex = li;
                        (winnerHand[wi], loserHand[li]) = (loserHand[li], winnerHand[wi]);
                    }
                }

                return Task.CompletedTask;
            }

            public RoundEventSnapshot ToSnapshot() => new()
            {
                EventType = "five_swap_hand_cards",
                Description = DescribeEvent(),
                SourcePlayer = _loser,
                TargetPlayers = [_winner, _loser],
                Data = new ()
                {
                    ["winnerHandIndex"] = _winnerIndex?.ToString() ?? string.Empty,
                    ["loserHandIndex"] = _loserIndex?.ToString() ?? string.Empty,
                }
            };
        }

        private sealed class QueenGrabKingEvent : IRoundEvent
        {
            private readonly Match _match;
            private readonly int _player;
            private bool _drewKing;

            public QueenGrabKingEvent(Match match, RoundContext ctx)
            {
                _match = match;
                _player = ctx.Loser ?? throw new InvalidOperationException("QueenGrabKingEvent requires a loser.");
            }

            public Task RecieveUserChoice() => Task.CompletedTask;

            public string DescribeEvent() => _drewKing
                ? $"Player {_player} drew a King from deck after their Queen was captured."
                : $"Player {_player} had no King to draw after their Queen was captured.";

            public Task ApplyEvent()
            {
                lock (_match)
                {
                    List<Card> deck = _match._Players[_player].hand.Deck;
                    List<int> kingIndexes = [];
                    for (int i = 0; i < deck.Count; i++)
                    {
                        if (CardExtensions.GetValue(deck[i]) == (int)CardExtensions.SpecialValues.King)
                        {
                            kingIndexes.Add(i);
                        }
                    }

                    if (kingIndexes.Count == 0)
                    {
                        _drewKing = false;
                        return Task.CompletedTask;
                    }

                    int selected = kingIndexes[Random.Shared.Next(0, kingIndexes.Count)];
                    Card king = deck[selected];
                    deck.RemoveAt(selected);
                    _match._Players[_player].hand.Cards.Add(king);
                    _drewKing = true;
                }

                return Task.CompletedTask;
            }

            public RoundEventSnapshot ToSnapshot() => new()
            {
                EventType = "queen_grab_king",
                Description = DescribeEvent(),
                SourcePlayer = _player,
                TargetPlayers = [_player],
                Data = new ()
                {
                    ["drewKing"] = _drewKing.ToString(),
                }
            };
        }

        private sealed class ForceDeckPlayEvent : IRoundEvent
        {
            private readonly Match _match;

            public ForceDeckPlayEvent(Match match, RoundContext ctx)
            {
                _ = ctx;
                _match = match;
            }

            public Task RecieveUserChoice() => Task.CompletedTask;

            public string DescribeEvent() => "All players must draw from deck next turn.";

            public Task ApplyEvent()
            {
                lock (_match)
                {
                    _match._forceDeckPlayNextTurn[0] = true;
                    _match._forceDeckPlayNextTurn[1] = true;
                }

                return Task.CompletedTask;
            }

            public RoundEventSnapshot ToSnapshot() => new()
            {
                EventType = "king_force_deck_play",
                Description = DescribeEvent(),
                SourcePlayer = null,
                TargetPlayers = [0, 1],
            };
        }
    }
}
