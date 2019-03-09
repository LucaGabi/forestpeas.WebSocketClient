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
            var serverTask = _webSocketServer.AcceptWebSocketAsync();

            using (var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125")))
            {
                var server = await serverTask;
                string message = "Hi!";
                await client.SendStringAsync(message);

                var buffer = new ArraySegment<byte>(new byte[1024]);
                WebSocketReceiveResult result = await server.ReceiveAsync(buffer, CancellationToken.None);
                Assert.True(result.MessageType == WebSocketMessageType.Text);
                Assert.True(message == Encoding.UTF8.GetString(buffer.Array, 0, result.Count));
            }
        }

        [Fact]
        public async Task ReceiveStringOK()
        {
            var serverTask = _webSocketServer.AcceptWebSocketAsync();

            using (var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125")))
            {
                var server = await serverTask;
                string sendMsg = "Hi!";
                var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(sendMsg));
                await server.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);

                string receivedMsg = await client.ReceiveStringAsync();
                Assert.True(sendMsg == receivedMsg);
            }
        }

        [Theory]
        [InlineData(128)]
        [InlineData(65536)]
        public async Task SendDataWithDifferentLengths(int length)
        {
            var serverTask = _webSocketServer.AcceptWebSocketAsync();

            using (var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125")))
            {
                var server = await serverTask;
                byte[] data = new byte[length];
                await client.SendByteArrayAsync(data);

                var buffer = new ArraySegment<byte>(new byte[length]);
                WebSocketReceiveResult result = await server.ReceiveAsync(buffer, CancellationToken.None);
                Assert.True(length == result.Count);
            }
        }

        [Theory]
        [InlineData(128)]
        [InlineData(65536)]
        public async Task ReceiveDataWithDifferentLengths(int length)
        {
            var serverTask = _webSocketServer.AcceptWebSocketAsync();

            using (var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125")))
            {
                var server = await serverTask;
                byte[] data = new byte[length];
                var buffer = new ArraySegment<byte>(data);
                await server.SendAsync(buffer, WebSocketMessageType.Binary, true, CancellationToken.None);

                var receivedMsg = await client.ReceiveByteArrayAsync();
                Assert.True(length == receivedMsg.Length);
            }
        }

        [Fact]
        public async Task ClientCloseFirst()
        {
            var serverTask = _webSocketServer.AcceptWebSocketAsync();
            var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125"));
            var server = await serverTask;

            // let server respond to client's close frame
            var serverCloseTask = server.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), CancellationToken.None);
            client.Dispose();
            await serverCloseTask;
            await client.CloseTask;
            Assert.Null(client.CloseException);
        }

        [Fact]
        public async Task ServerCloseFirst()
        {
            var serverTask = _webSocketServer.AcceptWebSocketAsync();
            var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125"));
            var server = await serverTask;

            await server.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            client.Dispose();
            await client.CloseTask;
            Assert.Null(client.CloseException);
        }
    }
}
