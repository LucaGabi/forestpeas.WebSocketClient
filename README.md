# Simple WebSockets client for .Net

# How to use

```c#
using (var client = await WsClient.ConnectAsync(new Uri("ws://localhost:8125")))
{
    await client.SendStringAsync("Hi!");
    string receivedMsg = await client.ReceiveStringAsync();
}
```
