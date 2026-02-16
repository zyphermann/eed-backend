using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

public sealed class S3FileUploader
{
    private readonly IAmazonS3 _s3;
    private readonly S3Options _options;
    private readonly ILogger<S3FileUploader> _logger;

    public S3FileUploader(IAmazonS3 s3, IOptions<S3Options> options, ILogger<S3FileUploader> logger)
    {
        _s3 = s3;
        _options = options.Value;
        _logger = logger;
    }

    public bool Enabled =>
        _options.Enabled && !string.IsNullOrWhiteSpace(_options.Bucket);

    public async Task UploadIfEnabledAsync(string path, string key, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var request = new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = key,
                FilePath = path,
                StorageClass = S3StorageClass.Standard
            };

            await _s3.PutObjectAsync(request, ct);
            _logger.LogInformation("Uploaded to S3 key={Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "S3 upload failed key={Key} path={Path}", key, path);
        }
    }
}
