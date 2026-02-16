using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.MapPost(
    "/echo",
    async (HttpRequest request) =>
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var contentType = request.ContentType ?? "application/octet-stream";
        return Results.Bytes(bytes, contentType);
    }
);

app.UseWebSockets();

app.MapGet(
    "/ws",
    async (HttpContext context) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<WsAudioIngestHandler>>();
        var handler = new WsAudioIngestHandler(logger);
        await handler.HandleAsync(context, null);
    }
);

app.MapGet(
    "/ws/{hwid}",
    async (HttpContext context, string hwid) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<WsAudioIngestHandler>>();
        var handler = new WsAudioIngestHandler(logger);
        await handler.HandleAsync(context, hwid);
    }
);

app.MapGet(
    "/ws/echo",
    async (HttpContext context) =>
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
);

app.Run();

static async Task<WsMessage?> ReceiveFullMessageAsync(
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

readonly record struct WsMessage(WebSocketMessageType MessageType, byte[] Payload);
