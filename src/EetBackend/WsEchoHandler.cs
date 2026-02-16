using System.Net.WebSockets;

public sealed class WsEchoHandler
{
    private readonly ILogger<WsEchoHandler> _logger;

    public WsEchoHandler(ILogger<WsEchoHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var message = await ReceiveFullMessageAsync(ws, buffer, context.RequestAborted);
            if (message is null)
            {
                break;
            }

            var msg = message.Value;
            if (msg.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "bye",
                    context.RequestAborted
                );
                break;
            }

            await ws.SendAsync(msg.Payload, msg.MessageType, true, context.RequestAborted);
        }
    }

    private static async Task<WsMessage?> ReceiveFullMessageAsync(
        WebSocket ws,
        byte[] buffer,
        CancellationToken ct
    )
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult? result = null;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new WsMessage(result.MessageType, Array.Empty<byte>());
            }

            if (result.Count > 0)
            {
                ms.Write(buffer, 0, result.Count);
            }
        } while (!result.EndOfMessage);

        return new WsMessage(result.MessageType, ms.ToArray());
    }

    private readonly record struct WsMessage(WebSocketMessageType MessageType, byte[] Payload);
}
