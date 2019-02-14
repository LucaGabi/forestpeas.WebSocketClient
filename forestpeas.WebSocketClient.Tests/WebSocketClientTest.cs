using Ninja.WebSockets;
using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace forestpeas.WebSocketClient.Tests
{
    public class WebSocketServerFixture : IDisposable
    {
        private readonly TcpListener _tcpListener;

        public WebSocketServerFixture()
        {
            _tcpListener = new TcpListener(IPAddress.Parse("127.0.0.1"), 8125);
            _tcpListener.Start();
        }

        public async Task<WebSocket> AcceptWebSocketAsync()
        {
            var tcpClient = await _tcpListener.AcceptTcpClientAsync();
            var factory = new WebSocketServerFactory();
            WebSocketHttpContext context = await factory.ReadHttpHeaderFromStreamAsync(tcpClient.GetStream());
            Assert.True(context.IsWebSocketRequest);
            return await factory.AcceptWebSocketAsync(context);
        }

        public void Dispose()
        {
            _tcpListener.Stop();
        }
    }

    public class WebSocketClientTest : IClassFixture<WebSocketServerFixture>
    {
        WebSocketServerFixture _webSocketServer;

        public WebSocketClientTest(WebSocketServerFixture webSocketServer)
        {
            _webSocketServer = webSocketServer;
        }

        [Fact]
        public async Task SendStringOK()
        {
            var serverSocketTask = _webSocketServer.AcceptWebSocketAsync();

            using (var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125")))
            {
                var serverSocket = await serverSocketTask;
                string message = "Hi!";
                await client.SendStringAsync(message);

                var buffer = new ArraySegment<byte>(new byte[1024]);
                WebSocketReceiveResult result = await serverSocket.ReceiveAsync(buffer, CancellationToken.None);
                Assert.True(result.MessageType == WebSocketMessageType.Text);
                Assert.True(message == Encoding.UTF8.GetString(buffer.Array, 0, result.Count));
            }
        }
    }
}
