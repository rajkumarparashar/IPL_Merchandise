using IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.Application.Common;

public static class Paging
{
    /// <summary>Missing → default; above the cap → clamped to the cap; below 1 → 400.</summary>
    public static int NormalisePageSize(int? requested)
    {
        if (requested is null) return ApiLimits.DefaultPageSize;
        if (requested < 1) throw RequestValidationException.For("pageSize", "Page size must be at least 1.");
        return Math.Min(requested.Value, ApiLimits.MaxPageSize);
    }
}
