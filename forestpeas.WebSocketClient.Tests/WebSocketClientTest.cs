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
    public class WebSocketClientTest
    {
        [Fact]
        public async Task SendStringOK()
        {
            var tcpListener = new TcpListener(IPAddress.Parse("127.0.0.1"), 8125);
            tcpListener.Start();
            var tcpClientTask = tcpListener.AcceptTcpClientAsync();
            var serverSocketTask = Task.Run(async () =>
            {
                var tcpClient = await tcpClientTask;
                var factory = new WebSocketServerFactory();
                WebSocketHttpContext context = await factory.ReadHttpHeaderFromStreamAsync(tcpClient.GetStream());
                Assert.True(context.IsWebSocketRequest);
                return await factory.AcceptWebSocketAsync(context);
            });

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

            tcpListener.Stop();
        }
    }
}
