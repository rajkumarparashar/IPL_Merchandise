using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.UnitTests.DomainModel;

public class ProductImageRulesTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, ImageFormat.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, ImageFormat.Png)]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, ImageFormat.WebP)]
    public void The_format_is_read_from_the_file_content(byte[] content, ImageFormat expected) =>
        Assert.Equal(expected, ProductImageRules.EnsureValid(content));

    [Theory]
    [InlineData("GIF89a")]            // a real image format we do not accept
    [InlineData("<svg></svg>")]       // can carry script
    [InlineData("RIFF....WAVE")]      // RIFF, but audio
    [InlineData("")]
    public void Anything_else_is_rejected(string content)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => ProductImageRules.EnsureValid(System.Text.Encoding.ASCII.GetBytes(content)));
        Assert.Equal(ErrorCodes.InvalidImage, ex.Code);
    }

    [Fact]
    public void Files_over_the_size_limit_are_rejected_even_if_they_are_images()
    {
        var big = new byte[ProductImageRules.MaxBytes + 1];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF;
        Assert.Throws<BusinessRuleException>(() => ProductImageRules.EnsureValid(big));
    }
}
