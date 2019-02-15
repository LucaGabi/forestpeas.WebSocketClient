﻿using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace forestpeas.WebSocketClient
{
    public sealed class WsClient : IDisposable
    {
        private readonly NetworkStream _networkStream;
        private bool _disposed = false;

        private WsClient(NetworkStream networkStream)
        {
            _networkStream = networkStream ?? throw new ArgumentNullException(nameof(networkStream));
        }

        public static async Task<WsClient> ConnectAsync(Uri uri)
        {
            if (uri.Scheme.ToLower() == "wss")
            {
                throw new NotSupportedException();
            }

            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(uri.Host, uri.Port).ConfigureAwait(false);
            var networkStream = tcpClient.GetStream(); // TODO: disose the stream

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

        public async Task<string> ReceiveStringAsync()
        {
            byte[] buffer = new byte[2];
            await ReadStreamAsync(buffer, 2).ConfigureAwait(false);

            byte firstByte = buffer[0];
            bool isFinBitSet = (firstByte & 0x80) == 0x80;
            if (!isFinBitSet)
            {
                throw new NotImplementedException("work in progress");
            }

            int opCode = firstByte & 0x0F;

            byte secondByte = buffer[1];
            bool isMaskBitSet = (secondByte & 0x80) == 0x80;
            if (isMaskBitSet)
            {
                // TODO: according to spec, close the connection.
            }

            int payloadLength = secondByte & 0x7F;
            if (payloadLength == 126)
            {
                await ReadStreamAsync(buffer, 2).ConfigureAwait(false);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(buffer);
                }
                payloadLength = BitConverter.ToUInt16(buffer, 0);
            }
            else if (payloadLength == 127)
            {
                buffer = new byte[8];
                await ReadStreamAsync(buffer, 8).ConfigureAwait(false);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(buffer);
                }
                ulong lengthUInt64 = BitConverter.ToUInt64(buffer, 0);

                ulong maxLength = 1024 * 1024 * 10;// TODO: change to property that can be set
                if (lengthUInt64 > maxLength)
                {
                    throw new InvalidOperationException($"Payload length cannot exceed{maxLength}");
                }
                if (lengthUInt64 > int.MaxValue) // for simplicity for now (Stream.Read does not accept a ulong count parameter)
                {
                    throw new NotSupportedException($"Payload length cannot exceed{int.MaxValue}");
                }

                payloadLength = (int)lengthUInt64;
            }

            byte[] payload = new byte[payloadLength];
            await ReadStreamAsync(payload, payloadLength).ConfigureAwait(false);

            switch (opCode) // TODO: complete other types of opCode
            {
                case 1: // text frame
                    return Encoding.UTF8.GetString(payload);
                default:
                    throw new NotSupportedException($"Unknown opcode \"{opCode}\" from server.");
            }
        }

        public async Task SendStringAsync(string message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            using (MemoryStream memoryStream = new MemoryStream())
            {
                byte fin = 0x80;
                byte opCode = 1; // text frame
                byte firstByte = (byte)(fin | opCode);
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
                await memoryStream.CopyToAsync(_networkStream).ConfigureAwait(false);
            }
        }

        private async Task ReadStreamAsync(byte[] buffer, int count)
        {
            int bytesRead = await _networkStream.ReadAsync(buffer, 0, count).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("Server closed connection.");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _networkStream.Dispose();
                _disposed = true;
            }
        }
    }
}
