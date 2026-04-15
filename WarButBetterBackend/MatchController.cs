using Microsoft.AspNetCore.Mvc;
using System.Collections.ObjectModel;
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
        private static readonly Queue<WaitingPlayer> WaitingPlayers = [];

        private sealed class WaitingPlayer
        {
            public required WebSocket Socket;
            public required TaskCompletionSource<Match> MatchFound;
        }

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

    
        [HttpGet]
        public async Task<IActionResult> WaitForMatch()
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                logger.LogWarning("Rejected non-websocket matchmaking request.");
                return BadRequest("Must be a websocket request");
            }

            WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("Accepted matchmaking websocket from {RemoteIp}.", HttpContext.Connection.RemoteIpAddress);
            var waiter = new WaitingPlayer
            {
                Socket = socket,
                MatchFound = new TaskCompletionSource<Match>(TaskCreationOptions.RunContinuationsAsynchronously),
            };

            try
            {
                lock (_matches)
                {
                    if (WaitingPlayers.Count > 0)
                    {
                        WaitingPlayer other = WaitingPlayers.Dequeue();
                        Match newMatch = CreateMatchLocked();
                        logger.LogInformation(
                            "Matched two waiting players into match {MatchId}. Remaining queue length: {QueueLength}.",
                            newMatch.Id,
                            WaitingPlayers.Count);
                        other.MatchFound.TrySetResult(newMatch);
                        waiter.MatchFound.TrySetResult(newMatch);
                    }
                    else
                    {
                        WaitingPlayers.Enqueue(waiter);
                        logger.LogInformation("Player queued for matchmaking. Queue length is now {QueueLength}.", WaitingPlayers.Count);
                    }
                }

                Match match = await waiter.MatchFound.Task.WaitAsync(HttpContext.RequestAborted);
                byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    type = "matchFound",
                    match.CreatedAt,
                    match.Id
                }));

                await socket.SendAsync(payload, WebSocketMessageType.Text, true, HttpContext.RequestAborted);
                return await _Connect(socket, match, false);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Matchmaking websocket request was canceled.");
                lock (WaitingPlayers)
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
                logger.LogDebug("Disposing matchmaking websocket.");
                socket.Dispose();
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
            if (HttpContext.WebSockets.IsWebSocketRequest)
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
                match.AddClient(session, !spectate);
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
