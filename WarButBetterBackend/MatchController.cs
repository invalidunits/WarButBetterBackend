using Microsoft.AspNetCore.Mvc;
using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WarButBetterBackend
{
    [ApiController]
    [Route("match")]
    public class MatchmakingController : ControllerBase
    {

        private static readonly Dictionary<Guid, Match> _matches = [];
        public Dictionary<Guid, Match> Matches => _matches;
        private static readonly Queue<WaitingPlayer> WaitingPlayers = [];
        private static readonly object MatchmakingLock = new();

        private sealed class WaitingPlayer
        {
            public required WebSocket Socket;
            public required TaskCompletionSource<Guid> MatchFound;
        }

        [HttpOptions]
        public IActionResult ListMatches()
        {
            lock (_matches)
            {
                return Ok(_matches.Keys);
            }
        }

        [HttpPost]
        public IActionResult CreateMatch()
        {
            lock (_matches)
            {
                int i = 0;
                Guid matchID;
                do
                {
                    if (++i == 5) throw new InvalidOperationException("Could not generate new unique guid");
                    matchID = Guid.NewGuid();
                } while (_matches.ContainsKey(matchID));
                _matches.Add(matchID, new Match());
                return Ok(matchID);
            }
        }

        [HttpGet]
        public async Task<IActionResult> WaitForMatch()
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                return BadRequest("Must be a websocket request");
            }

            WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            var waiter = new WaitingPlayer
            {
                Socket = socket,
                MatchFound = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously),
            };

            try
            {
                lock (MatchmakingLock)
                {
                    if (WaitingPlayers.Count > 0)
                    {
                        WaitingPlayer other = WaitingPlayers.Dequeue();
                        Guid newMatchId = CreateMatchLocked();
                        other.MatchFound.TrySetResult(newMatchId);
                        waiter.MatchFound.TrySetResult(newMatchId);
                    }
                    else if (FindMatchNeedingPlayer() is (Guid existingId, _))
                    {
                        waiter.MatchFound.TrySetResult(existingId);
                    }
                    else
                    {
                        WaitingPlayers.Enqueue(waiter);
                    }
                }

                Guid matchID = await waiter.MatchFound.Task.WaitAsync(HttpContext.RequestAborted);

                byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    type = "matchFound",
                    matchID,
                }));

                await socket.SendAsync(payload, WebSocketMessageType.Text, true, HttpContext.RequestAborted);

                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "match-assigned", CancellationToken.None);
                }

                return new EmptyResult();
            }
            catch (OperationCanceledException)
            {
                lock (MatchmakingLock)
                {
                    if (waiter.MatchFound.Task.IsCompleted)
                    {
                        throw;
                    }

                    // Remove canceled waiter if they are still queued.
                    int count = WaitingPlayers.Count;
                    for (int i = 0; i < count; i++)
                    {
                        WaitingPlayer item = WaitingPlayers.Dequeue();
                        if (!ReferenceEquals(item, waiter))
                        {
                            WaitingPlayers.Enqueue(item);
                        }
                    }
                }

                return new EmptyResult();
            }
            finally
            {
                socket.Dispose();
            }
        }

        private Guid CreateMatchLocked()
        {
            // Must be called under MatchmakingLock.
            int i = 0;
            Guid matchID;
            do
            {
                if (++i == 5) throw new InvalidOperationException("Could not generate new unique guid");
                matchID = Guid.NewGuid();
            } while (_matches.ContainsKey(matchID));

            _matches.Add(matchID, new Match());
            return matchID;
        }

        // Returns the ID of an existing match that is waiting for a second player.
        // Must be called under MatchmakingLock.
        private static (Guid id, Match match)? FindMatchNeedingPlayer()
        {
            foreach (var pair in _matches)
            {
                if (pair.Value.State == Match.MatchState.WaitingForPlayers && pair.Value.ConnectedPlayerCount == 1)
                    return (pair.Key, pair.Value);
            }
            return null;
        }

        public sealed class MatchCleanupService(ILogger<MatchCleanupService> logger) : BackgroundService
        {
            private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
            private static readonly TimeSpan GameEndGrace = TimeSpan.FromMinutes(2);
            private static readonly TimeSpan EmptyMatchExpiry = TimeSpan.FromMinutes(30);

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                using var timer = new PeriodicTimer(CleanupInterval);
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    CleanupMatches();
                }
            }

            private void CleanupMatches()
            {
                var now = DateTimeOffset.UtcNow;
                var toRemove = new List<Guid>();

                lock (MatchmakingLock)
                {
                    foreach (var pair in _matches)
                    {
                        Match match = pair.Value;
                        bool gameFinished = match.Game?.IsCompleted == true
                                           && now - match.CreatedAt > GameEndGrace;
                        bool abandoned = match.State == Match.MatchState.WaitingForPlayers
                                         && match.ConnectedPlayerCount == 0
                                         && now - match.CreatedAt > EmptyMatchExpiry;
                        if (gameFinished || abandoned)
                            toRemove.Add(pair.Key);
                    }

                    foreach (Guid id in toRemove)
                        _matches.Remove(id);
                }

                if (toRemove.Count > 0)
                    logger.LogInformation("Cleaned up {Count} stale match(es).", toRemove.Count);
            }
        }

        [HttpGet("{matchID}")]
        public async Task<IActionResult> Connect(Guid matchID, [FromQuery] bool spectate)
        {
            Match? match = null;
            lock (_matches)
            {
                if (_matches.ContainsKey(matchID))
                {
                    match = _matches[matchID];
                }
            }

            if (match is null) return NotFound();
            
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                var session = new ClientSession(socket);

                try
                {
                    match.AddClient(session, !spectate);
                    await session.Completion;
                    return new EmptyResult();
                }
                catch (InvalidOperationException ex)
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, ex.Message, CancellationToken.None);
                    }

                    session.Dispose();
                    return Conflict(ex.Message);
                }
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return BadRequest("Must be a websocket request");
            }
        }
    }
}
