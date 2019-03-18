using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("forestpeas.WebSocketClient.Tests")]

namespace forestpeas.WebSocketClient
{
    /// <summary>
    /// WebSocket client for sending and receiving messages.
    /// </summary>
    public sealed class WsClient : IDisposable
    {
        private readonly NetworkStream _networkStream;
        private bool _isCloseSent = false;
        private bool _isCloseReceived = false;
        private bool _disposed = false;

        private WsClient(NetworkStream networkStream)
        {
            _networkStream = networkStream ?? throw new ArgumentNullException(nameof(networkStream));
        }

        internal Task CloseTask { get; private set; }

        internal Exception CloseException { get; private set; }

        /// <summary>
        /// Gets or sets the max payload length.
        /// </summary>
        public ulong MaxPayloadLength { get; set; } = 1024 * 1024 * 10;

        /// <summary>
        /// Connect to a WebSocket servr with the specified Uri. 
        /// </summary>
        /// <param name="uri">The Uri of a WebSocket servr to connect to.</param>
        public static async Task<WsClient> ConnectAsync(Uri uri)
        {
            if (uri.Scheme.ToLower() == "wss")
            {
                throw new NotSupportedException();
            }

            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(uri.Host, uri.Port).ConfigureAwait(false);
            var networkStream = tcpClient.GetStream();

            try
            {
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
                await networkStream.WriteAsync(requestBytes, 0, requestBytes.Length).ConfigureAwait(false);

                using (StreamReader reader = new StreamReader(networkStream, Encoding.UTF8, true, 1024, true))
                {
                    string responseLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (responseLine != "HTTP/1.1 101 Switching Protocols")
                    {
                        throw new InvalidOperationException("Unexpected response line from server: " + responseLine);
                    }

                    while (true)
                    {
                        responseLine = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (responseLine == null) // end of stream
                        {
                            throw new EndOfStreamException("Server closed connection.");
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

                return new WsClient(networkStream);
            }
            catch (Exception)
            {
                networkStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Receive a text frame.
        /// </summary>
        public Task<string> ReceiveStringAsync()
        {
            return ReceiveStringAsync(CancellationToken.None);
        }

        /// <summary>
        /// Receive a binary frame.
        /// </summary>
        public Task<byte[]> ReceiveByteArrayAsync()
        {
            return ReceiveByteArrayAsync(CancellationToken.None);
        }

        /// <summary>
        /// Receive a text frame.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        public async Task<string> ReceiveStringAsync(CancellationToken cancellationToken)
        {
            var dataFrame = await ReceiveDataFrameAsync(cancellationToken).ConfigureAwait(false);
            CheckOpCode(dataFrame.OpCode, OpCode.TextFrame);
            return Encoding.UTF8.GetString(dataFrame.Payload);
        }

        /// <summary>
        /// Receive a binary frame.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        public async Task<byte[]> ReceiveByteArrayAsync(CancellationToken cancellationToken)
        {
            var dataFrame = await ReceiveDataFrameAsync(cancellationToken).ConfigureAwait(false);
            CheckOpCode(dataFrame.OpCode, OpCode.BinaryFrame);
            return dataFrame.Payload;
        }

        private void CheckOpCode(OpCode received, OpCode expected)
        {
            if (received == expected) return;

            // If we want to respond to a Ping frame as soon as possible, we must read network stream forever in a while loop,
            // and put frames other than Ping in a buffered stream. If called from a user of this class, we read from the buffered stream.
            // So this buffered stream must be thread safe, and would block util there is data available, and preferably release
            // resouces when data has been read.
            switch (received)
            {
                case OpCode.ConnectionClose:
                    throw new InvalidOperationException("Received close frame from server.");

                default:
                    throw new InvalidOperationException($"Expect \"{expected}\" but received \"{received}\".");
            }
        }

        /// <summary>
        /// Send a text message.
        /// </summary>
        /// <param name="message">A text message sent to the server.</param>
        public Task SendStringAsync(string message)
        {
            return SendStringAsync(message, CancellationToken.None);
        }

        /// <summary>
        /// Send a binary message.
        /// </summary>
        /// <param name="message">A binary message sent to the server.</param>
        public Task SendByteArrayAsync(byte[] message)
        {
            return SendByteArrayAsync(message, CancellationToken.None);
        }

        /// <summary>
        /// Send a text message.
        /// </summary>
        /// <param name="message">A text message sent to the server.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns></returns>
        public Task SendStringAsync(string message, CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            return SendDataFrameAsync(OpCode.TextFrame, payload, cancellationToken);
        }

        /// <summary>
        /// Send a binary message.
        /// </summary>
        /// <param name="message">A binary message sent to the server.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns></returns>
        public Task SendByteArrayAsync(byte[] message, CancellationToken cancellationToken)
        {
            return SendDataFrameAsync(OpCode.BinaryFrame, message, cancellationToken);
        }

        private async Task CloseAsync()
        {
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    await SendCloseFrameAsync(cts.Token).ConfigureAwait(false);

                    if (!_isCloseReceived)
                    {
                        // wait for close frame from server
                        while (true)
                        {
                            var dataFrame = await ReceiveDataFrameAsync(cts.Token).ConfigureAwait(false);
                            if (dataFrame.OpCode == OpCode.ConnectionClose) break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    CloseException = ex;
                }
            }

            _networkStream.Dispose();
        }

        private async Task<DataFrame> ReceiveDataFrameAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[2];
            await ReadStreamAsync(buffer, 2, cancellationToken).ConfigureAwait(false);

            byte firstByte = buffer[0];
            bool isFinBitSet = (firstByte & 0x80) == 0x80;
            if (!isFinBitSet)
            {
                throw new NotImplementedException("work in progress"); // TODO: continuation frame
            }

            int opCode = firstByte & 0x0F;

            byte secondByte = buffer[1];
            bool isMaskBitSet = (secondByte & 0x80) == 0x80;
            if (isMaskBitSet)
            {
                // A client MUST close a connection if it detects a masked frame.
                // In this case, it MAY use the status code 1002(protocol error) as defined in Section 7.4.1.
                await SendCloseFrameAsync(CancellationToken.None, WebSocketCloseStatus.ProtocolError);
            }

            int payloadLength = secondByte & 0x7F;
            if (payloadLength == 126)
            {
                await ReadStreamAsync(buffer, 2, cancellationToken).ConfigureAwait(false);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(buffer);
                }
                payloadLength = BitConverter.ToUInt16(buffer, 0);
            }
            else if (payloadLength == 127)
            {
                buffer = new byte[8];
                await ReadStreamAsync(buffer, 8, cancellationToken).ConfigureAwait(false);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(buffer);
                }
                ulong lengthUInt64 = BitConverter.ToUInt64(buffer, 0);

                if (lengthUInt64 > MaxPayloadLength)
                {
                    throw new InvalidOperationException($"Payload length cannot exceed{MaxPayloadLength}");
                }
                if (lengthUInt64 > int.MaxValue) // for simplicity (Stream.Read does not accept a ulong count parameter)
                {
                    throw new NotSupportedException($"Payload length cannot exceed{int.MaxValue}");
                }

                payloadLength = (int)lengthUInt64;
            }

            byte[] payload = new byte[payloadLength];
            await ReadStreamAsync(payload, payloadLength, cancellationToken).ConfigureAwait(false);

            if ((OpCode)opCode == OpCode.ConnectionClose)
            {
                _isCloseReceived = true;
                await SendCloseFrameAsync(cancellationToken).ConfigureAwait(false);
            }

            return new DataFrame(isFinBitSet, (OpCode)opCode, payload);
        }

        private async Task SendDataFrameAsync(OpCode opCode, byte[] payload, CancellationToken cancellationToken)
        {
            using (MemoryStream memoryStream = new MemoryStream())
            {
                byte fin = 0x80;
                byte firstByte = (byte)(fin | (byte)opCode);
                memoryStream.WriteByte(firstByte);

                byte mask = 0x80; // cient must mask the payload
                if (payload.Length < 126)
                {
                    byte secondByte = (byte)(mask | payload.Length);
                    memoryStream.WriteByte(secondByte);
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    byte secondByte = (byte)(mask | 126);
                    memoryStream.WriteByte(secondByte);

                    byte[] buffer = BitConverter.GetBytes((ushort)payload.Length);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(buffer);
                    }
                    memoryStream.Write(buffer, 0, buffer.Length);
                }
                else
                {
                    byte secondByte = (byte)(mask | 127);
                    memoryStream.WriteByte(secondByte);

                    byte[] buffer = BitConverter.GetBytes((ulong)payload.Length);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(buffer);
                    }
                    memoryStream.Write(buffer, 0, buffer.Length);
                }

                byte[] maskingKey = new byte[4];
                new Random().NextBytes(maskingKey);
                memoryStream.Write(maskingKey, 0, maskingKey.Length);

                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)(payload[i] ^ maskingKey[i % 4]);
                }

                memoryStream.Write(payload, 0, payload.Length);
                memoryStream.Seek(0, SeekOrigin.Begin);
                await memoryStream.CopyToAsync(_networkStream, 81920, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReadStreamAsync(byte[] buffer, int count, CancellationToken cancellationToken)
        {
            if (count == 0) return;

            int bytesRead = await _networkStream.ReadAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("Server closed connection.");
            }
        }

        private async Task SendCloseFrameAsync(CancellationToken cancellationToken, WebSocketCloseStatus closeStatus = WebSocketCloseStatus.Empty, string closeReason = null)
        {
            if (_isCloseSent) return;

            byte[] statusBuffer = BitConverter.GetBytes((ushort)closeStatus);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(statusBuffer);
            }

            byte[] payload;
            if (closeReason != null)
            {
                byte[] reasonBuffer = Encoding.UTF8.GetBytes(closeReason);
                payload = new byte[statusBuffer.Length + reasonBuffer.Length];
                Buffer.BlockCopy(statusBuffer, 0, payload, 0, statusBuffer.Length);
                Buffer.BlockCopy(reasonBuffer, 0, payload, statusBuffer.Length, reasonBuffer.Length);
            }
            else
            {
                payload = statusBuffer;
            }

            await SendDataFrameAsync(OpCode.ConnectionClose, payload, cancellationToken).ConfigureAwait(false);
            _isCloseSent = true;
        }

        /// <summary>
        /// Close the WebSocket Connection.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                CloseTask = CloseAsync();
                _disposed = true;
            }
        }
    }
}
