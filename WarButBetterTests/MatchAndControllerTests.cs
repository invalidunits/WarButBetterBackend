using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WarButBetterBackend;

namespace WarButBetterTests;

public class MatchTests
{
    private static Match CreateMatch() => new(Guid.NewGuid());
    private static ClientSession CreateSession() => new(new FakeOpenWebSocket(), NullLogger<ClientSession>.Instance);

    [Fact]
    public void NewMatch_StartsInWaitingForPlayersState()
    {
        var match = CreateMatch();

        Assert.Equal(Match.MatchState.WaitingForPlayers, match.State);
    }

    [Fact]
    public void AddClient_AsPlayer_AllowsOnlyTwoPlayers()
    {
        var match = CreateMatch();

        using var firstPlayer = CreateSession();
        using var secondPlayer = CreateSession();
        using var thirdPlayer = CreateSession();

        match.AddClient(firstPlayer, AsPlayer: true);
        match.AddClient(secondPlayer, AsPlayer: true);

        var exception = Assert.Throws<InvalidOperationException>(() => match.AddClient(thirdPlayer, AsPlayer: true));
        Assert.Contains("No more remaining spots", exception.Message);
    }

    [Fact]
    public void AddClient_AsPlayer_ReturnsAssignedPlayerIndex()
    {
        var match = CreateMatch();

        using var firstPlayer = CreateSession();
        using var secondPlayer = CreateSession();

        int? firstIndex = match.AddClient(firstPlayer, AsPlayer: true);
        int? secondIndex = match.AddClient(secondPlayer, AsPlayer: true);

        Assert.Equal(0, firstIndex);
        Assert.Equal(1, secondIndex);
    }

    [Fact]
    public void RemoveClient_FreesPlayerSlot()
    {
        var match = CreateMatch();

        using var firstPlayer = CreateSession();
        using var secondPlayer = CreateSession();
        using var replacementPlayer = CreateSession();

        match.AddClient(firstPlayer, AsPlayer: true);
        match.AddClient(secondPlayer, AsPlayer: true);

        match.RemoveClient(firstPlayer);

        var addReplacement = Record.Exception(() => match.AddClient(replacementPlayer, AsPlayer: true));
        Assert.Null(addReplacement);
    }
}

public class MatchmakingControllerTests
{
    private static MatchmakingController CreateController() =>
        new(NullLogger<MatchmakingController>.Instance, NullLogger<ClientSession>.Instance);

    private static Match CreateMatch() => new(Guid.NewGuid());

    [Fact]
    public void ListMatches_ReturnsExistingMatchIds()
    {
        var controller = CreateController();
        var expectedId = Guid.NewGuid();
        controller.Matches[expectedId] = CreateMatch();

        var result = controller.ListMatches();

        var ok = Assert.IsType<OkObjectResult>(result);
        var ids = Assert.IsAssignableFrom<IEnumerable<Guid>>(ok.Value);
        Assert.Contains(expectedId, ids);
    }

    [Fact]
    public void CreateMatch_ReturnsGuidAndStoresMatch()
    {
        var controller = CreateController();

        var result = controller.CreateMatch();

        var ok = Assert.IsType<OkObjectResult>(result);
        var id = Assert.IsType<Guid>(ok.Value);
        Assert.True(controller.Matches.ContainsKey(id));
    }

    [Fact]
    public async Task WaitForMatch_WithTwoHttpCallers_ReturnsSameMatchId()
    {
        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        Task<IActionResult> firstResultTask = controller.WaitForMatch();

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        IActionResult secondResult = await controller.WaitForMatch();
        IActionResult firstResult = await firstResultTask;

        var firstOk = Assert.IsType<OkObjectResult>(firstResult);
        var secondOk = Assert.IsType<OkObjectResult>(secondResult);
        Guid firstMatchId = ExtractMatchId(firstOk);
        Guid secondMatchId = ExtractMatchId(secondOk);

        Assert.NotEqual(Guid.Empty, firstMatchId);
        Assert.Equal(firstMatchId, secondMatchId);
    }

    private static Guid ExtractMatchId(OkObjectResult response)
    {
        string json = JsonSerializer.Serialize(response.Value);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Connect_WhenMatchNotFound_ReturnsNotFound()
    {
        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await controller.Connect(Guid.NewGuid(), spectate: false);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Connect_WhenNotWebSocketRequest_ReturnsBadRequestAnd400()
    {
        var controller = CreateController();
        var matchId = Guid.NewGuid();
        controller.Matches[matchId] = CreateMatch();

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await controller.Connect(matchId, spectate: true);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Must be a websocket request", badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }
}

public class MatchRoundHistorySerializationTests
{
    private static Match CreateMatch() => new(Guid.NewGuid());

    [Fact]
    public void GetRoundHistory_NewMatch_IsEmpty()
    {
        var match = CreateMatch();

        var history = match.GetRoundHistory();

        Assert.Empty(history);
    }

    [Fact]
    public void RoundContext_SerializesClientFacingFields()
    {
        var ctx = new Match.RoundContext
        {
            Turn = 4,
            Played = new byte[] { 1, 2 },
            Pile = new List<byte> { 1, 2, 3, 4 },
            AppliedEvents = new List<Match.RoundEventSnapshot>
            {
                new()
                {
                    EventType = "three_choose_top_five",
                    Description = "Player 1 chose index 2 from top-five and moved card 17 to top of deck (effect of captured 3).",
                    SourcePlayer = 1,
                    TargetPlayers = new [] { 1 },
                    Data = new ()
                    {
                        ["selectedIndex"] = "2",
                        ["chosenCard"] = "17",
                    }
                }
            },
            EffectsDisabled = false,
            OutcomeCode = 0,
            Winner = 0,
            RemainingCards = new [] { 26, 24 },
            BurnedCardsTotal = 2,
        };

        string json = JsonSerializer.Serialize(ctx);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(4, root.GetProperty("Turn").GetInt32());
        Assert.Equal(0, root.GetProperty("OutcomeCode").GetInt32());
        Assert.Equal(0, root.GetProperty("Winner").GetInt32());
        Assert.Equal(1, root.GetProperty("Loser").GetInt32());
        Assert.Equal(2, root.GetProperty("BurnedCardsTotal").GetInt32());

        JsonElement events = root.GetProperty("AppliedEvents");
        Assert.Equal(JsonValueKind.Array, events.ValueKind);
        Assert.Single(events.EnumerateArray());

        JsonElement firstEvent = events[0];
        Assert.Equal("three_choose_top_five", firstEvent.GetProperty("EventType").GetString());
        Assert.Equal(1, firstEvent.GetProperty("SourcePlayer").GetInt32());

        Assert.False(root.TryGetProperty("Events", out _));
    }
}

public class MatchBattleRuleTests
{
    private static Match CreateMatch() => new(Guid.NewGuid());

    [Fact]
    public void DetermineWinner_AceVsNumber_AceCapturesInNormalRound()
    {
        var match = CreateMatch();
        byte ace = CardExtensions.GetCard(CardExtensions.Suite.Heart, 14);
        byte nine = CardExtensions.GetCard(CardExtensions.Suite.Spade, 9);

        object outcome = InvokeDetermineWinner(match, ace, nine, warMode: false);

        Assert.Equal("Player0Capture", outcome.ToString());
    }

    [Fact]
    public void DetermineWinner_JokerStillBurns()
    {
        var match = CreateMatch();
        byte joker = CardExtensions.GetCard(CardExtensions.Suite.Jester, 0);
        byte ace = CardExtensions.GetCard(CardExtensions.Suite.Heart, 14);

        object outcome = InvokeDetermineWinner(match, joker, ace, warMode: false);

        Assert.Equal("JokerBurn", outcome.ToString());
    }

    [Fact]
    public async Task QueueCardEffects_AceVsFour_AppliesOpposingCardEffect()
    {
        var match = CreateMatch();
        byte ace = CardExtensions.GetCard(CardExtensions.Suite.Heart, 14);
        byte four = CardExtensions.GetCard(CardExtensions.Suite.Spade, 4);
        var ctx = new Match.RoundContext
        {
            Turn = 1,
            Played = [ace, four],
            Pile = [ace, four],
            AppliedEvents = [],
            EffectsDisabled = false,
            OutcomeCode = 0,
            Winner = 0,
            RemainingCards = [26, 26],
            BurnedCardsTotal = 0,
        };

        var queueMethod = typeof(Match).GetMethod("QueueCardEffects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(queueMethod);
        queueMethod!.Invoke(match, [ctx]);

        var applyMethod = typeof(Match).GetMethod("ApplyQueuedEffects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);
        Task applyTask = Assert.IsAssignableFrom<Task>(applyMethod!.Invoke(match, [ctx]));
        await applyTask;

        Assert.Contains(ctx.AppliedEvents, e => e.EventType == "four_shuffle_opponent_deck");
    }

    private static object InvokeDetermineWinner(Match match, byte player0Card, byte player1Card, bool warMode)
    {
        var method = typeof(Match).GetMethod("DetermineWinner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        object? outcome = method!.Invoke(match, [player0Card, player1Card, warMode]);
        Assert.NotNull(outcome);
        return outcome!;
    }
}

internal sealed class FakeOpenWebSocket : WebSocket
{
    private WebSocketState _state = WebSocketState.Open;

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new OperationCanceledException(cancellationToken);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
