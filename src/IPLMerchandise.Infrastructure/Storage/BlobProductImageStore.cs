using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Domain.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPLMerchandise.Infrastructure.Storage;

/// <summary>
/// Product photos in Azure Blob Storage. Every upload gets a new random name and is never overwritten, so files can be
/// cached for a year by browsers and CDNs, and order history keeps pointing at the photo the customer saw.
/// </summary>
public sealed class BlobProductImageStore(BlobServiceClient service, IOptions<ImageStorageOptions> options, ILogger<BlobProductImageStore> logger)
    : IProductImageStore
{
    private const string CacheControl = "public, max-age=31536000, immutable";

    private readonly ImageStorageOptions _options = options.Value;
    private readonly BlobContainerClient _container = service.GetBlobContainerClient(options.Value.ContainerName);
    private volatile bool _containerReady;

    public async Task<ProductImage> SaveAsync(string productId, ReadOnlyMemory<byte> content, ImageFormat format, CancellationToken ct)
    {
        // The name is ours, never the uploader's file name: no path tricks, no clashes, no personal data in URLs.
        var key = $"{Uri.EscapeDataString(productId)}/{Guid.NewGuid():N}{ProductImageRules.ExtensionOf(format)}";
        var contentType = ProductImageRules.ContentTypeOf(format);
        try
        {
            await EnsureContainerAsync(ct);
            await _container.GetBlobClient(key).UploadAsync(BinaryData.FromBytes(content), new BlobUploadOptions
            {
                // Content type comes from the detected format, not from the request, so browsers render it only as an image.
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType, CacheControl = CacheControl },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } // never overwrite an existing file
            }, ct);
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "Product image upload failed ({Status} {ErrorCode})", ex.Status, ex.ErrorCode);
            throw new DependencyUnavailableException("Image storage is unavailable right now. Please try again shortly.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("Image storage did not respond in time. Please try again.", ex);
        }

        return new ProductImage(key, PublicUrl(key), contentType);
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);

    private string PublicUrl(string key)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.PublicBaseUrl) ? _container.Uri.ToString() : _options.PublicBaseUrl;
        return $"{baseUrl.TrimEnd('/')}/{key}";
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerReady || !_options.CreateContainerIfMissing) return;
        // Blob-level public read: anyone with a file's URL can load it (product photos are public), but nobody can list the container.
        await _container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);
        _containerReady = true;
    }
}
