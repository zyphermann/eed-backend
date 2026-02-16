using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTransient<WsAudioIngestHandler>();
builder.Services.AddTransient<WsEchoHandler>();
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
    async (HttpContext context, WsAudioIngestHandler handler) =>
    {
        await handler.HandleAsync(context, null);
    }
);

// Audio ingest endpoint with hardwareId in the path.
app.MapGet(
    "/ws/{hardwareId}",
    async (HttpContext context, string hardwareId, WsAudioIngestHandler handler) =>
    {
        await handler.HandleAsync(context, hardwareId);
    }
);

app.MapGet(
    "/ws/echo",
    async (HttpContext context, WsEchoHandler handler) =>
    {
        await handler.HandleAsync(context);
    }
);

app.Run();
