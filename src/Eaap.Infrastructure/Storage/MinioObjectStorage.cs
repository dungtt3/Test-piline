using Amazon.S3;
using Amazon.S3.Model;
using Eaap.Application;
using Microsoft.Extensions.Options;

namespace Eaap.Infrastructure.Storage;

/// <summary>MinIO-backed object storage using the S3 API.</summary>
public class MinioObjectStorage(IAmazonS3 s3, IOptions<MinioOptions> options) : IObjectStorage
{
    private readonly string _bucket = options.Value.Bucket;

    public async Task UploadAsync(string key, Stream content, long length, string contentType, CancellationToken ct = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = contentType,
            Headers = { ContentLength = length }
        };
        await s3.PutObjectAsync(request, ct);
    }

    public async Task<Stream> DownloadAsync(string key, CancellationToken ct = default)
    {
        var response = await s3.GetObjectAsync(_bucket, key, ct);
        // Buffer into memory so the HTTP response can be disposed deterministically.
        var buffer = new MemoryStream();
        await using (response.ResponseStream)
        {
            await response.ResponseStream.CopyToAsync(buffer, ct);
        }
        buffer.Position = 0;
        return buffer;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(_bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
