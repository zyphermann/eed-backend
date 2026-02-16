public sealed class S3Options
{
    public bool Enabled { get; set; } = false;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "eu-west-1";
    public string Prefix { get; set; } = "received";
    public bool UploadBin { get; set; } = true;
    public bool UploadWav { get; set; } = true;
}
