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

// Audio ingest endpoint (PCM or ADPCM).
app.MapGet(
    "/ws",
    async (HttpContext context) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<WsAudioIngestHandler>>();
        var handler = new WsAudioIngestHandler(logger);
        await handler.HandleAsync(context, null);
    }
);

// Audio ingest endpoint with HWID in the path.
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
        var logger = context.RequestServices.GetRequiredService<ILogger<WsEchoHandler>>();
        var handler = new WsEchoHandler(logger);
        await handler.HandleAsync(context);
    }
);

app.Run();
