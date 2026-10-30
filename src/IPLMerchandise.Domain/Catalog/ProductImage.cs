using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.Domain.Catalog;

/// <summary>A product photo held in blob storage. <see cref="Key"/> is the blob name; <see cref="Url"/> is what browsers load.</summary>
public sealed record ProductImage(string Key, string Url, string ContentType);

public enum ImageFormat { Jpeg, Png, WebP }

/// <summary>
/// Which uploads are accepted as product photos. The format is decided from the file's own bytes, never from the
/// file name or the Content-Type the browser sent, so a script renamed to .png is rejected.
/// </summary>
public static class ProductImageRules
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static readonly IReadOnlyList<string> AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>Returns the format, or throws <see cref="BusinessRuleException"/> with code invalid-image.</summary>
    public static ImageFormat EnsureValid(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
            throw Invalid("The image file is empty.");
        if (content.Length > MaxBytes)
            throw Invalid($"The image must be at most {MaxBytes / (1024 * 1024)} MB.");
        return Detect(content) ?? throw Invalid("Only JPEG, PNG or WebP images are accepted.");
    }

    /// <summary>Recognises the file by its signature ("magic bytes").</summary>
    public static ImageFormat? Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return ImageFormat.Jpeg;
        if (content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ImageFormat.Png;
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8)) return ImageFormat.WebP;
        return null;
    }

    public static string ContentTypeOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        _ => "image/webp"
    };

    public static string ExtensionOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        _ => ".webp"
    };

    private static BusinessRuleException Invalid(string message) => new(ErrorCodes.InvalidImage, message, "file");
}
