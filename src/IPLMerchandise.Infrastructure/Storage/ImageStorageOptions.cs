using System.ComponentModel.DataAnnotations;

namespace IPLMerchandise.Infrastructure.Storage;

public sealed class ImageStorageOptions
{
    public const string Section = "ImageStorage";

    /// <summary>Azure Storage connection string. Azurite locally; a secret store (or managed identity) in production.</summary>
    [Required]
    public string ConnectionString { get; set; } = "";

    [Required, RegularExpression("^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$", ErrorMessage = "Not a valid blob container name.")]
    public string ContainerName { get; set; } = "product-images";

    /// <summary>
    /// Base URL browsers use to load images, e.g. a CDN in front of the container. Defaults to the container URL.
    /// Needed with Docker, where the API reaches Azurite as "azurite" but browsers reach it as "localhost".
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Create the container (with anonymous read of individual files) on first upload. Development only; production provisions it.</summary>
    public bool CreateContainerIfMissing { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 15;
}
