# Simple WebSocket client for .Net

A simple WebSocket client implementation with asynchronous APIs.

# How to use

```c#
using (var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125")))
{
    await client.SendStringAsync("Hi!");
    string receivedMsg = await client.ReceiveStringAsync();
	
    await client.SendByteArrayAsync(Encoding.UTF8.GetBytes("Hi!"));
    byte[] receivedBytes = await client.ReceiveByteArrayAsync();
}
```
