using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WarButBetterBackend
{
    [ApiController]
    [Route("match")]
    public class MatchmakingController(ILogger<MatchmakingController> logger, ILogger<ClientSession> clientLogger) : ControllerBase
    {

        private static readonly Dictionary<Guid, Match> _matches = [];
        public Dictionary<Guid, Match> Matches => _matches;
        private static readonly Queue<TaskCompletionSource<Match>> WaitingPlayers = [];

        [HttpOptions]
        public IActionResult ListMatches()
        {
            lock (_matches) return Ok(_matches.Keys.ToArray());
        }

        [HttpPost]
        public IActionResult CreateMatch()
        {
            lock (_matches)
            {
                Match match = CreateMatchLocked();
                logger.LogInformation("Created match {MatchId} at {CreatedAt} via HTTP POST.", match.Id, match.CreatedAt);
                return Ok(match.Id);
            }
        }

        [HttpGet("{matchID}/status")]
        public IActionResult GetMatchStatus(Guid matchID)
        {
            Match? match;
            lock (_matches)
            {
                if (!_matches.TryGetValue(matchID, out match))
                {
                    return NotFound();
                }
            }

            Match.MatchState state = match.State;
            int connectedPlayers = match.ConnectedPlayerCount;
            bool canJoinAsPlayer = state == Match.MatchState.WaitingForPlayers && connectedPlayers < 2;

            return Ok(new
            {
                id = match.Id,
                state,
                connectedPlayers,
                maxPlayers = 2,
                createdAt = match.CreatedAt,
                canJoinAsPlayer,
            });
        }

    
        [HttpGet]
        public async Task<IActionResult> WaitForMatch()
        {
            var waiter = new TaskCompletionSource<Match>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                lock (_matches)
                {
                    if (WaitingPlayers.Count > 0)
                    {
                        var other = WaitingPlayers.Dequeue();
                        Match newMatch = CreateMatchLocked();
                        logger.LogInformation(
                            "Matched two waiting players into match {MatchId}. Remaining queue length: {QueueLength}.",
                            newMatch.Id,
                            WaitingPlayers.Count);
                        other.TrySetResult(newMatch);
                        waiter.TrySetResult(newMatch);
                    }
                    else
                    {
                        WaitingPlayers.Enqueue(waiter);
                        logger.LogInformation("Player queued for matchmaking. Queue length is now {QueueLength}.", WaitingPlayers.Count);
                    }
                }

                Match match = await waiter.Task.WaitAsync(HttpContext.RequestAborted);
                return Ok(new
                {
                    type = "matchFound",
                    id = match.Id,
                    createdAt = match.CreatedAt,
                });
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("HTTP matchmaking request was canceled.");
                lock (_matches)
                {
                    if (waiter.Task.IsCompleted)
                    {
                        throw;
                    }

                    // Remove canceled waiter if they are still queued.
                    int count = WaitingPlayers.Count;
                    for (int i = 0; i < count; i++)
                    {
                        TaskCompletionSource<Match> item = WaitingPlayers.Dequeue();
                        if (!ReferenceEquals(item, waiter))
                        {
                            WaitingPlayers.Enqueue(item);
                        }
                    }
                }

                return NoContent();
            }
        }

        private Match CreateMatchLocked()
        {
            int i = 0;
            Guid matchID;
            do
            {
                if (++i == 5) throw new InvalidOperationException("Could not generate new unique guid");
                matchID = Guid.NewGuid();
            } while (_matches.ContainsKey(matchID));

            var match = new Match(matchID);
            _matches.Add(matchID, match);
            return match;
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

            if (match is null)
            {
                logger.LogWarning("Connection attempt for missing match {MatchId}.", matchID);
                return NotFound();
            }

            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                logger.LogInformation(
                    "Accepted websocket connect for match {MatchId}. Spectator mode: {Spectate}.",
                    matchID,
                    spectate);
                WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                return await _Connect(socket, match, spectate);
            }
            else
            {
                logger.LogWarning("Rejected non-websocket connect request for match {MatchId}.", matchID);
                return BadRequest("Must be a websocket request");
            }
        }
    

        [NonAction]
        public async Task<IActionResult> _Connect(WebSocket socket, Match match, bool spectate)
        {
            var session = new ClientSession(socket, clientLogger);
            logger.LogInformation(
                "Client session starting for match {MatchId}. Player slot requested: {IsPlayer}.",
                match.Id,
                !spectate);

            try
            {
                int? playerIndex = match.AddClient(session, !spectate);
                if (socket.State == WebSocketState.Open)
                {
                    byte[] joinedPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        type = "joinedMatch",
                        matchId = match.Id,
                        player = playerIndex,
                        spectate,
                    }));
                    await socket.SendAsync(joinedPayload, WebSocketMessageType.Text, true, CancellationToken.None);
                }

                await session.Completion;
                logger.LogInformation("Client session completed for match {MatchId}.", match.Id);
                return new EmptyResult();
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Client session rejected for match {MatchId}: {Reason}", match.Id, ex.Message);
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, ex.Message, CancellationToken.None);
                }

                session.Dispose();
                return Conflict(ex.Message);
            }
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

                lock (_matches)
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
                        {
                            logger.LogDebug(
                                "Scheduling match {MatchId} for cleanup. Finished: {GameFinished}, Abandoned: {Abandoned}.",
                                pair.Key,
                                gameFinished,
                                abandoned);
                            toRemove.Add(pair.Key);
                        }
                    }

                    foreach (Guid id in toRemove)
                        _matches.Remove(id);
                }

                if (toRemove.Count > 0)
                    logger.LogInformation("Cleaned up {Count} stale match(es).", toRemove.Count);
            }
        }
    }
}
