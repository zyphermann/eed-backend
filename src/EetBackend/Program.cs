using Amazon;
using Amazon.S3;
using DotNetEnv;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
var contentRoot = builder.Environment.ContentRootPath;
var projectRoot = Directory.GetParent(contentRoot)?.Parent?.FullName ?? contentRoot;
Env.Load(Path.Combine(projectRoot, ".env"));
builder.Services.Configure<S3Options>(builder.Configuration.GetSection("S3"));
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var options = sp.GetRequiredService<IOptions<S3Options>>().Value;
    var region = RegionEndpoint.GetBySystemName(options.Region);
    return new AmazonS3Client(region);
});
builder.Services.AddSingleton<S3FileUploader>();
builder.Services.AddTransient<WsAudioIngestHandler>();
builder.Services.AddTransient<WsEchoHandler>();
var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var s3Options = app.Services.GetRequiredService<IOptions<S3Options>>().Value;
var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
var hasAccessKey = !string.IsNullOrWhiteSpace(accessKey);
var accessKeySuffix = hasAccessKey && accessKey!.Length >= 4
    ? accessKey[^4..]
    : "";
startupLogger.LogInformation(
    "S3 config enabled={Enabled} bucket={Bucket} region={Region} prefix={Prefix} upload_bin={UploadBin} upload_wav={UploadWav} aws_access_key_present={HasKey} aws_access_key_last4={Last4}",
    s3Options.Enabled,
    s3Options.Bucket,
    s3Options.Region,
    s3Options.Prefix,
    s3Options.UploadBin,
    s3Options.UploadWav,
    hasAccessKey,
    accessKeySuffix
);

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
