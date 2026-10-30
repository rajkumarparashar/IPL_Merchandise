using IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.Web.Client.State;

/// <summary>
/// Per-order limit for a product. <see cref="ProductDto.MaxQuantityPerLine"/> was added to v1 later (DESIGN ADR-8), so an
/// older API may not send it; then the documented default applies: 2 for limited editions, otherwise 10. The API enforces
/// the limit either way; this only keeps the UI from offering what would be refused.
/// </summary>
public static class ProductLimits
{
    public const int DefaultPerLine = 10;
    public const int DefaultLimitedEditionPerLine = 2;

    public static int PerLine(ProductDto product) =>
        product.MaxQuantityPerLine ?? (product.IsLimitedEdition ? DefaultLimitedEditionPerLine : DefaultPerLine);
}
