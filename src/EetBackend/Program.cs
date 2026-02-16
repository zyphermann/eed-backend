using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using DotNetEnv;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
var contentRoot = builder.Environment.ContentRootPath;
var projectRoot = Directory.GetParent(contentRoot)?.Parent?.FullName ?? contentRoot;
var envPath = Path.Combine(projectRoot, ".env");
Env.Load(envPath);

// Map S3_PROVIDER into S3__Provider for configuration binding.
var s3Provider = Environment.GetEnvironmentVariable("S3_PROVIDER") ?? "aws";
Environment.SetEnvironmentVariable("S3__Provider", s3Provider);

// Map Scaleway env vars to AWS-compatible ones if provider=scaleway and AWS vars are not set.
if (string.Equals(s3Provider, "scaleway", StringComparison.OrdinalIgnoreCase))
{
    var scwAccess = Environment.GetEnvironmentVariable("SCW_ACCESS_KEY");
    var scwSecret = Environment.GetEnvironmentVariable("SCW_SECRET_KEY");
    if (!string.IsNullOrWhiteSpace(scwAccess) && !string.IsNullOrWhiteSpace(scwSecret))
    {
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", scwAccess);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", scwSecret);
    }
}

// Map provider-specific bucket/region settings into S3__* if not already set.
static void SetIfEmpty(string key, string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return;
    }

    var existing = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(existing))
    {
        Environment.SetEnvironmentVariable(key, value);
    }
}

// Map single-underscore env vars to .NET configuration keys.
SetIfEmpty("S3__Enabled", Environment.GetEnvironmentVariable("S3_ENABLED"));
SetIfEmpty("S3__Prefix", Environment.GetEnvironmentVariable("S3_PREFIX"));
SetIfEmpty("S3__UploadBin", Environment.GetEnvironmentVariable("S3_UPLOADBIN"));
SetIfEmpty("S3__UploadWav", Environment.GetEnvironmentVariable("S3_UPLOADWAV"));
if (string.Equals(s3Provider, "scaleway", StringComparison.OrdinalIgnoreCase))
{
    Environment.SetEnvironmentVariable("S3_BUCKET", Environment.GetEnvironmentVariable("SCW_BUCKET"));
    Environment.SetEnvironmentVariable("S3_REGION", Environment.GetEnvironmentVariable("SCW_REGION"));
    Environment.SetEnvironmentVariable("S3_SERVICE_URL", Environment.GetEnvironmentVariable("SCW_SERVICE_URL"));
    Environment.SetEnvironmentVariable(
        "S3_FORCE_PATH_STYLE",
        Environment.GetEnvironmentVariable("SCW_FORCE_PATH_STYLE")
    );
}
else
{
    SetIfEmpty("S3_BUCKET", Environment.GetEnvironmentVariable("AWS_BUCKET"));
    SetIfEmpty("S3_REGION", Environment.GetEnvironmentVariable("AWS_REGION"));
}

SetIfEmpty("S3__Bucket", Environment.GetEnvironmentVariable("S3_BUCKET"));
SetIfEmpty("S3__Region", Environment.GetEnvironmentVariable("S3_REGION"));
SetIfEmpty("S3__ServiceUrl", Environment.GetEnvironmentVariable("S3_SERVICE_URL"));
SetIfEmpty("S3__ForcePathStyle", Environment.GetEnvironmentVariable("S3_FORCE_PATH_STYLE"));

// Push env values into configuration explicitly (avoids timing issues).
void SetConfigIfPresent(string key, string envName)
{
    var value = Environment.GetEnvironmentVariable(envName);
    if (!string.IsNullOrWhiteSpace(value))
    {
        builder.Configuration[key] = value;
    }
}

SetConfigIfPresent("S3:Provider", "S3__Provider");
SetConfigIfPresent("S3:Enabled", "S3__Enabled");
SetConfigIfPresent("S3:Bucket", "S3__Bucket");
SetConfigIfPresent("S3:Region", "S3__Region");
SetConfigIfPresent("S3:ServiceUrl", "S3__ServiceUrl");
SetConfigIfPresent("S3:ForcePathStyle", "S3__ForcePathStyle");
SetConfigIfPresent("S3:Prefix", "S3__Prefix");
SetConfigIfPresent("S3:UploadBin", "S3__UploadBin");
SetConfigIfPresent("S3:UploadWav", "S3__UploadWav");

builder.Configuration.AddEnvironmentVariables();

// Provider-specific values are now mapped above.
builder.Services.Configure<S3Options>(builder.Configuration.GetSection("S3"));
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var options = sp.GetRequiredService<IOptions<S3Options>>().Value;
    if (string.Equals(options.Provider, "scaleway", StringComparison.OrdinalIgnoreCase))
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region
        };
        var accessKeyId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        if (!string.IsNullOrWhiteSpace(accessKeyId) && !string.IsNullOrWhiteSpace(secretKey))
        {
            var creds = new BasicAWSCredentials(accessKeyId, secretKey);
            return new AmazonS3Client(creds, config);
        }

        return new AmazonS3Client(config);
    }

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
    "Env load path={EnvPath} exists={EnvExists} S3_PROVIDER={S3Provider} S3_ENABLED={S3Enabled} S3_BUCKET={S3Bucket}",
    envPath,
    File.Exists(envPath),
    Environment.GetEnvironmentVariable("S3_PROVIDER") ?? "-",
    Environment.GetEnvironmentVariable("S3_ENABLED") ?? "-",
    Environment.GetEnvironmentVariable("S3_BUCKET") ?? "-"
);
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
startupLogger.LogInformation(
    "S3 config provider={Provider} service_url={ServiceUrl} force_path_style={ForcePathStyle} auth_region={AuthRegion}",
    s3Options.Provider,
    s3Options.ServiceUrl ?? "-",
    s3Options.ForcePathStyle,
    s3Options.Region
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
