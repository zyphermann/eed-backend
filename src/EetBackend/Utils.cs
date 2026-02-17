using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;

public static class Utils
{
    public static void LoadEnvironmentVariables(WebApplicationBuilder builder)
    {
        // Map S3_PROVIDER into S3__Provider for configuration binding.
        var s3Provider = Environment.GetEnvironmentVariable("S3_PROVIDER") ?? "aws";
        Environment.SetEnvironmentVariable("S3__Provider", s3Provider);

        // Map single-underscore env vars to .NET configuration keys.
        Utils.SetIfEmpty("S3__Enabled", Environment.GetEnvironmentVariable("S3_ENABLED"));
        Utils.SetIfEmpty("S3__Prefix", Environment.GetEnvironmentVariable("S3_PREFIX"));
        Utils.SetIfEmpty("S3__UploadBin", Environment.GetEnvironmentVariable("S3_UPLOADBIN"));
        Utils.SetIfEmpty("S3__UploadWav", Environment.GetEnvironmentVariable("S3_UPLOADWAV"));

        if (string.Equals(s3Provider, "scaleway", StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable(
                "AWS_ACCESS_KEY_ID",
                Environment.GetEnvironmentVariable("SCW_ACCESS_KEY")
            );
            Environment.SetEnvironmentVariable(
                "AWS_SECRET_ACCESS_KEY",
                Environment.GetEnvironmentVariable("SCW_SECRET_KEY")
            );

            Environment.SetEnvironmentVariable(
                "S3_BUCKET",
                Environment.GetEnvironmentVariable("SCW_BUCKET")
            );
            Environment.SetEnvironmentVariable(
                "S3_REGION",
                Environment.GetEnvironmentVariable("SCW_REGION")
            );
            Environment.SetEnvironmentVariable(
                "S3_SERVICE_URL",
                Environment.GetEnvironmentVariable("SCW_SERVICE_URL")
            );
            Environment.SetEnvironmentVariable(
                "S3_FORCE_PATH_STYLE",
                Environment.GetEnvironmentVariable("SCW_FORCE_PATH_STYLE")
            );
        }
        else
        {
            Utils.SetIfEmpty("S3_BUCKET", Environment.GetEnvironmentVariable("AWS_BUCKET"));
            Utils.SetIfEmpty("S3_REGION", Environment.GetEnvironmentVariable("AWS_REGION"));
        }

        Utils.SetIfEmpty("S3__Bucket", Environment.GetEnvironmentVariable("S3_BUCKET"));
        Utils.SetIfEmpty("S3__Region", Environment.GetEnvironmentVariable("S3_REGION"));
        Utils.SetIfEmpty("S3__ServiceUrl", Environment.GetEnvironmentVariable("S3_SERVICE_URL"));
        Utils.SetIfEmpty(
            "S3__ForcePathStyle",
            Environment.GetEnvironmentVariable("S3_FORCE_PATH_STYLE")
        );

        SetConfigIfPresent("S3:Provider", "S3__Provider", builder);
        SetConfigIfPresent("S3:Enabled", "S3__Enabled", builder);
        SetConfigIfPresent("S3:Bucket", "S3__Bucket", builder);
        SetConfigIfPresent("S3:Region", "S3__Region", builder);
        SetConfigIfPresent("S3:ServiceUrl", "S3__ServiceUrl", builder);
        SetConfigIfPresent("S3:ForcePathStyle", "S3__ForcePathStyle", builder);
        SetConfigIfPresent("S3:Prefix", "S3__Prefix", builder);
        SetConfigIfPresent("S3:UploadBin", "S3__UploadBin", builder);
        SetConfigIfPresent("S3:UploadWav", "S3__UploadWav", builder);

        builder.Configuration.AddEnvironmentVariables();
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
                    AuthenticationRegion = options.Region,
                };
                var accessKeyId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
                if (
                    !string.IsNullOrWhiteSpace(accessKeyId) && !string.IsNullOrWhiteSpace(secretKey)
                )
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
    }

    // Map provider-specific bucket/region settings into S3__* if not already set.
    public static void SetIfEmpty(string key, string? value)
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

    public static void SetConfigIfPresent(string key, string envName, WebApplicationBuilder builder)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Configuration[key] = value;
        }
    }
}
