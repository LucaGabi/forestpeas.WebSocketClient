using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace forestpeas.WebSocketClient
{
    internal sealed class WebSocketClient
    {
        private WebSocketClient() { }

        public static async Task<WebSocketClient> ConnectAsync(Uri uri)
        {
            if (uri.Scheme.ToLower() == "wss")
            {
                throw new NotSupportedException();
            }

            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(uri.Host, uri.Port);
            var connectionStream = tcpClient.GetStream(); // TODO: disose the stream

            // handshake
            Random rand = new Random();
            byte[] secWebSocketKeyBytes = new byte[16];
            rand.NextBytes(secWebSocketKeyBytes);
            string secWebSocketKey = Convert.ToBase64String(secWebSocketKeyBytes);
            string request = $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
                             $"Host: {uri.Host}:{uri.Port}\r\n" +
                             $"Upgrade: websocket\r\n" +
                             $"Connection: Upgrade\r\n" +
                             $"Sec-WebSocket-Key: {secWebSocketKey}\r\n" +
                             $"Sec-WebSocket-Version: 13\r\n" +
                             $"Origin: http://{uri.Host}:{uri.Port}\r\n\r\n";
            byte[] requestBytes = Encoding.UTF8.GetBytes(request);
            connectionStream.Write(requestBytes, 0, requestBytes.Length);

            using (StreamReader reader = new StreamReader(connectionStream, Encoding.UTF8, true, 1024, true))
            {
                string responseLine = await reader.ReadLineAsync();
                if (responseLine != "HTTP/1.1 101 Switching Protocols")
                {
                    throw new InvalidOperationException("Unexpected response line from server: " + responseLine);
                }

                while (true)
                {
                    responseLine = await reader.ReadLineAsync();
                    if (responseLine == null) // end of stream
                    {
                        throw new InvalidOperationException("Server closed connection.");
                    }
                    if (responseLine == string.Empty) break; // finished reading headers

                    var header = responseLine.Split(':');
                    string headerName = header[0].Trim();
                    string headerValue = (header.Length > 1) ? header[1].Trim() : string.Empty;

                    if (headerName == "Sec-WebSocket-Accept")
                    {
                        byte[] appendedBytes = Encoding.UTF8.GetBytes(secWebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
                        string expectedAcceptValue = Convert.ToBase64String(SHA1.Create().ComputeHash(appendedBytes));
                        if (expectedAcceptValue != headerValue)
                        {
                            throw new InvalidOperationException("Invalid Sec-WebSocket-Accept value from server.");
                        }
                    }
                }
            }

            return new WebSocketClient();
        }
    }
}
