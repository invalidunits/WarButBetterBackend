using System.Buffers;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace WarButBetterBackend
{
    public class ClientSession : IDisposable
    {
        public readonly CancellationTokenSource cancellationTokenSource;
        public readonly WebSocket webSocket;
        public bool Disposed { get; private set; }
        public bool Active => !networkTask.IsCompleted;
        private Task networkTask;
        public Task Completion => networkTask;
        private readonly ILogger<ClientSession> _logger;

        public ClientSession(WebSocket webSocket, ILogger<ClientSession> logger)
        {
            this.webSocket = webSocket;
            this.cancellationTokenSource = new CancellationTokenSource();
            _logger = logger;
            networkTask = Task.Run(Run);
            _logger.LogInformation("ClientSession created. WebSocket state: {State}.", webSocket.State);
        }  

        public delegate Task RecieveData_t(ClientSession client, ReadOnlyMemory<byte> data, WebSocketReceiveResult result);
        public event RecieveData_t RecieveData = static (_, _, _) => Task.CompletedTask;
        public event Action<ClientSession> Closed = delegate { };

        public async Task Run()
        {
            _logger.LogDebug("ClientSession network loop starting.");
            try
            {
                while (!cancellationTokenSource.IsCancellationRequested && webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
                {
                    WebSocketReceiveResult webSocketReceiveResult;
                    const int clientBufferSize = 256;
                    byte[] _buffer = ArrayPool<byte>.Shared.Rent(clientBufferSize);
                    try
                    {
                        webSocketReceiveResult = await webSocket.ReceiveAsync(
                                new ArraySegment<byte>(_buffer, 0, clientBufferSize), 
                                cancellationTokenSource.Token);

                        if (webSocketReceiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            
                            _logger.LogInformation("Received close frame from client: {CloseStatusDescription}", webSocketReceiveResult.CloseStatusDescription);
                            break;
                        }

                        _logger.LogDebug("Received {Count} byte(s) from client.", webSocketReceiveResult.Count);
                        await RecieveData(this, new ReadOnlyMemory<byte>(_buffer, 0, clientBufferSize), webSocketReceiveResult);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(_buffer);
                    }
                }

                _logger.LogInformation("ClientSession loop ended. Closing with NormalClosure.");
                await TryCloseAsync(WebSocketCloseStatus.NormalClosure, "session-complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in ClientSession network loop. Closing with InternalServerError.");
                await TryCloseAsync(WebSocketCloseStatus.InternalServerError, "session-error");
                webSocket.Abort();
            }
            finally
            {
                _logger.LogDebug("ClientSession firing Closed event.");
                Closed(this);
            }
            
        }

        private async Task TryCloseAsync(WebSocketCloseStatus status, string description)
        {
            if (webSocket.State != WebSocketState.Open && webSocket.State != WebSocketState.CloseReceived)
            {
                return;
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await webSocket.CloseAsync(status, description, cancellation.Token);
        }

        public void Dispose()
        {
            lock (this)
            {
                if (!Disposed)
                {
                    _logger.LogInformation("Disposing ClientSession. WebSocket state: {State}.", webSocket.State);
                    cancellationTokenSource.Cancel();
                    networkTask.Wait();

                    if (webSocket.State != WebSocketState.Closed)
                    {
                        _logger.LogDebug("WebSocket not closed on dispose (state: {State}); aborting.", webSocket.State);
                        webSocket.Abort();
                        webSocket.Dispose();
                    }

                    Disposed = true;
                    _logger.LogInformation("ClientSession disposed.");
                }
            }
        }
    }
}