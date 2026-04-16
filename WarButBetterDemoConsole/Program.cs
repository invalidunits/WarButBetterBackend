using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WarButBetterDemoConsole;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> Main(string[] args)
    {
        DemoOptions options = ParseOptions(args);
        Console.WriteLine($"Using backend: {options.BaseUri}");
        Console.WriteLine($"Mode: {options.Mode}");

        using var httpClient = new HttpClient
        {
            BaseAddress = options.BaseUri,
            Timeout = TimeSpan.FromMinutes(5),
        };

        try
        {
            switch (options.Mode)
            {
                case DemoMode.Api:
                    await RunHttpDemoAsync(httpClient);
                    await RunHttpMatchmakingDemoAsync(httpClient);
                    break;
                case DemoMode.Play:
                    await RunQueuedPlayerGameDemoAsync(httpClient, options.BaseUri, options.AutoPlay);
                    break;
                case DemoMode.PlayAgainstBot:
                    await RunPlayableGameDemoAsync(httpClient, options.BaseUri, options.AutoPlay);
                    break;
                case DemoMode.All:
                    await RunHttpDemoAsync(httpClient);
                    await RunHttpMatchmakingDemoAsync(httpClient);
                    await RunPlayableGameDemoAsync(httpClient, options.BaseUri, options.AutoPlay);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported mode: {options.Mode}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Demo failed: {ex.Message}");
            Console.Error.WriteLine("Tip: start the backend first (dotnet run --project WarButBetterBackend/WarButBetterBackend.csproj).\n" +
                                    "You can also provide a URL: dotnet run --project WarButBetterDemoConsole -- --base-url http://localhost:5151 --mode play-against-bot");
            return 1;
        }
    }

    private static DemoOptions ParseOptions(string[] args)
    {
        Uri baseUri = new("http://localhost:5151");
        DemoMode mode = DemoMode.Play;
        bool autoPlay = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--base-url", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for --base-url");
                }

                if (!Uri.TryCreate(args[i + 1], UriKind.Absolute, out Uri? parsed))
                {
                    throw new ArgumentException($"Invalid URL: {args[i + 1]}");
                }

                baseUri = parsed;
                i++;
                continue;
            }

            if (string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for --mode (play|play-against-bot|api|all)");
                }

                mode = args[i + 1].ToLowerInvariant() switch
                {
                    "play" => DemoMode.Play,
                    "play-against-bot" => DemoMode.PlayAgainstBot,
                    "playagainstbot" => DemoMode.PlayAgainstBot,
                    "api" => DemoMode.Api,
                    "all" => DemoMode.All,
                    _ => throw new ArgumentException($"Unknown mode: {args[i + 1]}. Expected play, play-against-bot, api, or all."),
                };

                i++;
                continue;
            }

            if (string.Equals(args[i], "--auto-play", StringComparison.OrdinalIgnoreCase))
            {
                autoPlay = true;
                continue;
            }

            throw new ArgumentException($"Unknown argument: {args[i]}");
        }

        return new DemoOptions(baseUri, mode, autoPlay);
    }

    private static async Task RunHttpDemoAsync(HttpClient httpClient)
    {
        Console.WriteLine("\n=== HTTP Demo ===");
        Guid createdMatch = await CreateMatchAsync(httpClient);
        Console.WriteLine($"Created match: {createdMatch}");

        IReadOnlyList<Guid> matches = await ListMatchesAsync(httpClient);
        Console.WriteLine($"List matches count: {matches.Count}");
        foreach (Guid matchId in matches)
        {
            Console.WriteLine($" - {matchId}");
        }
    }

    private static async Task RunHttpMatchmakingDemoAsync(HttpClient httpClient)
    {
        Console.WriteLine("\n=== HTTP Matchmaking Demo ===");
        Console.WriteLine("Starting two HTTP queue requests to /match...");

        Task<Guid> playerOne = WaitForMatchAsync(httpClient, "Player 1");
        await Task.Delay(200);
        Task<Guid> playerTwo = WaitForMatchAsync(httpClient, "Player 2");

        Guid[] matchIds = await Task.WhenAll(playerOne, playerTwo);
        Console.WriteLine($"Player 1 received matchID: {matchIds[0]}");
        Console.WriteLine($"Player 2 received matchID: {matchIds[1]}");
        Console.WriteLine(matchIds[0] == matchIds[1]
            ? "Matchmaking succeeded: both players got the same match ID."
            : "Warning: players received different match IDs.");
    }

    private static async Task RunQueuedPlayerGameDemoAsync(HttpClient httpClient, Uri baseUri, bool autoPlay)
    {
        Console.WriteLine("\n=== Queue Match (Random Opponent) ===");
        if (!autoPlay)
        {
            Console.WriteLine("Joining queue as Player 1. Enter a card index when prompted.");
        }

        Console.WriteLine("Waiting for opponent via HTTP queue endpoint /match");
        Guid matchId = await WaitForMatchAsync(httpClient, "Player 1");

        Console.WriteLine($"Matched! Match ID: {matchId}");

        Uri playerUri = BuildWebSocketUri(baseUri, $"match/{matchId}?spectate=false");
        using var playerSocket = new ClientWebSocket();
        await playerSocket.ConnectAsync(playerUri, CancellationToken.None);

        var state = new PlayerState("Player 1", playerNumber: 0, isBot: autoPlay);
        await RunPlayerLoopAsync(playerSocket, state, interactive: !autoPlay);
    }

    private static async Task RunPlayableGameDemoAsync(HttpClient httpClient, Uri baseUri, bool autoPlay)
    {
        Console.WriteLine("\n=== Playable Console Match ===");
        if (!autoPlay)
        {
            Console.WriteLine("You are Player 1. Enter a card index when prompted.");
        }

        Guid matchId = await CreateMatchAsync(httpClient);
        Uri wsUri = BuildWebSocketUri(baseUri, $"match/{matchId}?spectate=false");

        using var playerOneSocket = new ClientWebSocket();
        using var playerTwoSocket = new ClientWebSocket();
        await playerOneSocket.ConnectAsync(wsUri, CancellationToken.None);
        await playerTwoSocket.ConnectAsync(wsUri, CancellationToken.None);

        var playerOneState = new PlayerState("Player 1", playerNumber: 0, isBot: autoPlay);
        var playerTwoState = new PlayerState("Player 2", playerNumber: 1, isBot: true);

        Task t1 = RunPlayerLoopAsync(playerOneSocket, playerOneState, interactive: !autoPlay);
        Task t2 = RunPlayerLoopAsync(playerTwoSocket, playerTwoState, interactive: false);
        await Task.WhenAll(t1, t2);
    }

    private static async Task<Guid> WaitForMatchAsync(HttpClient httpClient, string playerName)
    {
        using HttpResponseMessage response = await httpClient.GetAsync("match");
        response.EnsureSuccessStatusCode();

        string payload = await response.Content.ReadAsStringAsync();
        MatchFoundMessage? message = JsonSerializer.Deserialize<MatchFoundMessage>(payload, JsonOptions);
        if (message is null || message.Id == Guid.Empty)
        {
            throw new InvalidOperationException($"{playerName} received invalid queue payload: {payload}");
        }

        return message.Id;
    }

    private static async Task<Guid> CreateMatchAsync(HttpClient httpClient)
    {
        using HttpResponseMessage response = await httpClient.PostAsync("match", content: null);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Guid>(body, JsonOptions);
    }

    private static async Task<IReadOnlyList<Guid>> ListMatchesAsync(HttpClient httpClient)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "match");
        using HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<Guid>>(body, JsonOptions) ?? [];
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Server closed websocket before sending match info.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task RunPlayerLoopAsync(ClientWebSocket socket, PlayerState state, bool interactive)
    {
        while (socket.State == WebSocketState.Open)
        {
            string payload = await ReceiveTextMessageAsync(socket);
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("type", out JsonElement typeNode))
            {
                continue;
            }

            string? messageType = typeNode.GetString();
            switch (messageType)
            {
                case "joinedMatch":
                    if (root.TryGetProperty("player", out JsonElement playerNode)
                        && playerNode.ValueKind == JsonValueKind.Number)
                    {
                        state.PlayerNumber = playerNode.GetInt32();
                    }
                    break;

                case "startingRoundIn":
                    await PrintStartingRoundInAsync(root, interactive, state.Name);
                    break;

                case "hand":
                    UpdateHandFromMessage(state, root);
                    if (interactive)
                    {
                        PrintHand(state);
                    }
                    break;

                case "requestTurn":
                    await HandleTurnRequestAsync(socket, state, interactive);
                    break;

                case "chooseTopFive":
                    await HandleTopFiveChoiceAsync(socket, state, root, interactive);
                    break;

                case "roundResult":
                    if (interactive)
                    {
                        PrintRoundResult(root);
                    }
                    break;

                case "jokerBurn":
                    if (interactive)
                    {
                        Console.WriteLine("Round ended with Joker burn.");
                    }
                    break;

                case "matchEnded":
                    PrintMatchEnded(root, interactive, state.Name);
                    await CloseIfOpenAsync(socket, "match-ended");
                    return;
            }
        }
    }

    private static void UpdateHandFromMessage(PlayerState state, JsonElement root)
    {
        string handBase64 = root.GetProperty("hand").GetString() ?? string.Empty;
        byte[] hand = Convert.FromBase64String(handBase64);

        state.Hand = hand.ToList();
        state.DeckLength = root.GetProperty("decklen").GetInt32();
        state.CurrentTurn = root.GetProperty("turn").GetInt32();
    }

    private static async Task HandleTurnRequestAsync(ClientWebSocket socket, PlayerState state, bool interactive)
    {
        int chosenIndex = state.Hand.Count == 0 ? 0 : Random.Shared.Next(0, state.Hand.Count);

        if (interactive)
        {
            Console.WriteLine();
            Console.WriteLine($"Turn {state.CurrentTurn}: choose card index (0-{Math.Max(0, state.Hand.Count - 1)}). Enter for random.");
            string? input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input)
                && int.TryParse(input, out int parsed)
                && parsed >= 0
                && parsed < state.Hand.Count)
            {
                chosenIndex = parsed;
            }
        }

        await SendJsonAsync(socket, new
        {
            type = "turn",
            cardIndex = chosenIndex,
        });

        if (state.Hand.Count > chosenIndex && chosenIndex >= 0)
        {
            byte played = state.Hand[chosenIndex];
            state.Hand.RemoveAt(chosenIndex);
            if (interactive)
            {
                Console.WriteLine($"Played: {WarButBetterBackend.CardExtensions.CardToString(played)}");
            }
        }
    }

    private static async Task HandleTopFiveChoiceAsync(ClientWebSocket socket, PlayerState state, JsonElement root, bool interactive)
    {
        int[] topFive = root.GetProperty("cards").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        int choice = 0;

        if (state.IsBot)
        {
            choice = Random.Shared.Next(0, topFive.Length);
        }
        else if (interactive)
        {
            Console.WriteLine("Choose from top deck cards:");
            for (int i = 0; i < topFive.Length; i++)
            {
                Console.WriteLine($"  [{i}] {WarButBetterBackend.CardExtensions.CardToString((byte)topFive[i])}");
            }

            Console.WriteLine("Enter choice index (default 0):");
            string? input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input)
                && int.TryParse(input, out int parsed)
                && parsed >= 0
                && parsed < topFive.Length)
            {
                choice = parsed;
            }
        }

        await SendJsonAsync(socket, new
        {
            type = "effectChoice",
            choice,
        });
    }

    private static void PrintHand(PlayerState state)
    {
        Console.WriteLine();
        Console.WriteLine($"{state.Name} hand (deck remaining: {state.DeckLength}):");
        if (state.Hand.Count == 0)
        {
            Console.WriteLine("  [empty]");
            return;
        }

        for (int i = 0; i < state.Hand.Count; i++)
        {
            Console.WriteLine($"  [{i}] {WarButBetterBackend.CardExtensions.CardToString(state.Hand[i])}");
        }
    }

    private static void PrintRoundResult(JsonElement root)
    {
        int turn = root.TryGetProperty("turn", out JsonElement turnNode) ? turnNode.GetInt32() : -1;
        int winner = root.TryGetProperty("winner", out JsonElement winnerNode) ? winnerNode.GetInt32() : -1;
        int[] remaining = root.TryGetProperty("remaining", out JsonElement remNode)
            ? remNode.EnumerateArray().Select(e => e.GetInt32()).ToArray()
            : [];

        Console.WriteLine();
        Console.WriteLine($"Round {turn} winner: Player {winner + 1}");
        if (remaining.Length == 2)
        {
            Console.WriteLine($"Remaining cards -> P1: {remaining[0]}, P2: {remaining[1]}");
        }
    }

    private static async Task PrintStartingRoundInAsync(JsonElement root, bool interactive, string playerName)
    {
        string prefix = interactive ? string.Empty : $"{playerName}: ";

        if (!root.TryGetProperty("time", out JsonElement timeNode) || timeNode.ValueKind != JsonValueKind.String)
        {
            Console.WriteLine($"{prefix}Match is starting soon...");
            return;
        }

        string? timeText = timeNode.GetString();
        if (string.IsNullOrWhiteSpace(timeText) || !DateTime.TryParse(timeText, out DateTime startUtc))
        {
            Console.WriteLine($"{prefix}Match is starting soon...");
            return;
        }

        DateTime startUtcNormalized = startUtc.ToUniversalTime();
        DateTime localStart = startUtcNormalized.ToLocalTime();

        if (!interactive)
        {
            TimeSpan remaining = startUtcNormalized - DateTime.UtcNow;
            int remainingSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            Console.WriteLine($"{prefix}Match starts in {remainingSeconds}s ({localStart:T})");
            return;
        }

        int previousLength = 0;
        int previousSeconds = int.MinValue;
        while (true)
        {
            TimeSpan remaining = startUtcNormalized - DateTime.UtcNow;
            int remainingSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));

            if (remainingSeconds != previousSeconds)
            {
                string message = $"{prefix}Match starts in {remainingSeconds}s ({localStart:T})";
                WriteOverwritableLine(message, ref previousLength);
                previousSeconds = remainingSeconds;
            }

            if (remainingSeconds == 0)
            {
                Console.WriteLine();
                break;
            }

            await Task.Delay(100);
        }
    }

    private static void WriteOverwritableLine(string message, ref int previousLength)
    {
        if (previousLength > 0)
        {
            Console.Write(new string('\b', previousLength));
        }

        Console.Write(message);

        if (message.Length < previousLength)
        {
            int toClear = previousLength - message.Length;
            Console.Write(new string(' ', toClear));
            Console.Write(new string('\b', toClear));
        }

        previousLength = message.Length;
    }

    private static void PrintMatchEnded(JsonElement root, bool interactive, string playerName)
    {
        int winner = root.TryGetProperty("winner", out JsonElement winnerNode) ? winnerNode.GetInt32() : -1;
        string reason = root.TryGetProperty("reason", out JsonElement reasonNode)
            ? (reasonNode.GetString() ?? "unknown")
            : "unknown";

        if (!interactive)
        {
            Console.WriteLine($"{playerName} observed match end. Winner: {winner + 1}, reason: {reason}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("=== Match Ended ===");
        if (winner < 0)
        {
            Console.WriteLine($"Result: tie ({reason})");
        }
        else
        {
            Console.WriteLine($"Winner: Player {winner + 1} ({reason})");
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task CloseIfOpenAsync(ClientWebSocket socket, string reason)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
    }

    private static Uri BuildWebSocketUri(Uri baseUri, string relativePath)
    {
        string scheme = baseUri.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            _ => throw new InvalidOperationException($"Unsupported scheme for backend URL: {baseUri.Scheme}"),
        };

        string path = relativePath;
        string query = string.Empty;
        int q = relativePath.IndexOf('?');
        if (q >= 0)
        {
            path = relativePath[..q];
            query = relativePath[(q + 1)..];
        }

        var builder = new UriBuilder(baseUri)
        {
            Scheme = scheme,
            Path = path.TrimStart('/'),
            Query = query,
        };

        return builder.Uri;
    }

    private sealed record MatchFoundMessage(string Type, DateTime CreatedAt, Guid Id);

    private enum DemoMode
    {
        Play,
        PlayAgainstBot,
        Api,
        All,
    }

    private sealed record DemoOptions(Uri BaseUri, DemoMode Mode, bool AutoPlay);

    private sealed class PlayerState
    {
        public PlayerState(string name, int playerNumber, bool isBot)
        {
            Name = name;
            PlayerNumber = playerNumber;
            IsBot = isBot;
        }

        public string Name { get; }
        public int PlayerNumber { get; set; }
        public bool IsBot { get; }
        public List<byte> Hand { get; set; } = [];
        public int DeckLength { get; set; }
        public int CurrentTurn { get; set; }
    }
}
