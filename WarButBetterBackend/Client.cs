using System.Buffers;
using System.Net.WebSockets;

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

        public ClientSession(WebSocket webSocket)
        {
            this.webSocket = webSocket;
            this.cancellationTokenSource = new CancellationTokenSource();
            networkTask = Task.Run(Run);
        }  

        public delegate void RecieveData_t(ClientSession client, Span<byte> data, WebSocketReceiveResult result);
        public event RecieveData_t RecieveData = delegate { };
        public event Action<ClientSession> Closed = delegate { };

        public async Task Run()
        {
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
                            break;
                        }

                        RecieveData(this, new Span<byte>(_buffer, 0, clientBufferSize), webSocketReceiveResult);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(_buffer);
                    }
                }

                await TryCloseAsync(WebSocketCloseStatus.NormalClosure, "session-complete");
            }
            catch (Exception)
            {
                await TryCloseAsync(WebSocketCloseStatus.InternalServerError, "session-error");
                webSocket.Abort();
            }
            finally
            {
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
                    cancellationTokenSource.Cancel();
                    networkTask.Wait();

                    if (webSocket.State != WebSocketState.Closed)
                    {
                        webSocket.Abort();
                        webSocket.Dispose();
                    }

                    Disposed = true; 
                }
            }
        }
    }
}