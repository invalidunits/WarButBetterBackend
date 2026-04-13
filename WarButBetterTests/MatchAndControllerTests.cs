using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarButBetterBackend;

namespace WarButBetterTests;

public class MatchTests
{
    [Fact]
    public void NewMatch_StartsInWaitingForPlayersState()
    {
        var match = new Match();

        Assert.Equal(Match.MatchState.WaitingForPlayers, match.State);
    }

    [Fact]
    public void AddClient_AsPlayer_AllowsOnlyTwoPlayers()
    {
        var match = new Match();

        using var firstPlayer = new ClientSession(new FakeClosedWebSocket());
        using var secondPlayer = new ClientSession(new FakeClosedWebSocket());
        using var thirdPlayer = new ClientSession(new FakeClosedWebSocket());

        match.AddClient(firstPlayer, AsPlayer: true);
        match.AddClient(secondPlayer, AsPlayer: true);

        var exception = Assert.Throws<InvalidOperationException>(() => match.AddClient(thirdPlayer, AsPlayer: true));
        Assert.Contains("No more remaining spots", exception.Message);
    }

    [Fact]
    public void RemoveClient_FreesPlayerSlot()
    {
        var match = new Match();

        using var firstPlayer = new ClientSession(new FakeClosedWebSocket());
        using var secondPlayer = new ClientSession(new FakeClosedWebSocket());
        using var replacementPlayer = new ClientSession(new FakeClosedWebSocket());

        match.AddClient(firstPlayer, AsPlayer: true);
        match.AddClient(secondPlayer, AsPlayer: true);

        match.RemoveClient(firstPlayer);

        var addReplacement = Record.Exception(() => match.AddClient(replacementPlayer, AsPlayer: true));
        Assert.Null(addReplacement);
    }
}

public class MatchmakingControllerTests
{
    [Fact]
    public void ListMatches_ReturnsExistingMatchIds()
    {
        var controller = new MatchmakingController();
        var expectedId = Guid.NewGuid();
        controller.Matches[expectedId] = new Match();

        var result = controller.ListMatches();

        var ok = Assert.IsType<OkObjectResult>(result);
        var ids = Assert.IsAssignableFrom<IEnumerable<Guid>>(ok.Value);
        Assert.Contains(expectedId, ids);
    }

    [Fact]
    public void CreateMatch_ReturnsGuidAndStoresMatch()
    {
        var controller = new MatchmakingController();

        var result = controller.CreateMatch();

        var ok = Assert.IsType<OkObjectResult>(result);
        var id = Assert.IsType<Guid>(ok.Value);
        Assert.True(controller.Matches.ContainsKey(id));
    }

    [Fact]
    public async Task WaitForMatch_WhenNotWebSocket_ReturnsBadRequest()
    {
        var controller = new MatchmakingController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.WaitForMatch();

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Must be a websocket request", badRequest.Value);
    }

    [Fact]
    public async Task Connect_WhenMatchNotFound_ReturnsNotFound()
    {
        var controller = new MatchmakingController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.Connect(Guid.NewGuid(), spectate: false);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Connect_WhenNotWebSocketRequest_ReturnsBadRequestAnd400()
    {
        var controller = new MatchmakingController();
        var matchId = Guid.NewGuid();
        controller.Matches[matchId] = new Match();

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await controller.Connect(matchId, spectate: true);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Must be a websocket request", badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, controller.HttpContext.Response.StatusCode);
    }
}

public class MatchRoundHistorySerializationTests
{
    [Fact]
    public void GetRoundHistory_NewMatch_IsEmpty()
    {
        var match = new Match();

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
                    Data = new Dictionary<string, string>
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

internal sealed class FakeClosedWebSocket : WebSocket
{
    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => WebSocketState.Closed;

    public override string? SubProtocol => null;

    public override void Abort()
    {
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Binary, endOfMessage: true));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
