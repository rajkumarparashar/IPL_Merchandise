namespace IPLMerchandise.Domain.Carts;

public static class CartRules
{
    public const int MinQuantityPerLine = 1;
    public const int MaxQuantityPerLine = 10;

    /// <summary>Limited editions (signed photographs) are capped lower so one buyer cannot take the whole run.</summary>
    public const int MaxLimitedEditionPerLine = 2;

    /// <summary>
    /// Checkout writes 2 operations per line + 1 commit record in a single Cosmos transactional batch,
    /// which is limited to 100 operations. 20 lines keeps us well inside that.
    /// </summary>
    public const int MaxLines = 20;
}
