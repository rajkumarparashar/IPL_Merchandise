namespace IPLMerchandise.Domain.Catalog;

public sealed record Franchise(string Code, string Name);

/// <summary>
/// Reference data. Franchises change roughly never, so a static list is simpler than a container.
/// With multi-tenancy (see DESIGN.md) this becomes the tenant registry.
/// </summary>
public static class Franchises
{
    public static readonly IReadOnlyList<Franchise> All =
    [
        new("CSK", "Chennai Super Kings"),
        new("DC", "Delhi Capitals"),
        new("GT", "Gujarat Titans"),
        new("KKR", "Kolkata Knight Riders"),
        new("LSG", "Lucknow Super Giants"),
        new("MI", "Mumbai Indians"),
        new("PBKS", "Punjab Kings"),
        new("RR", "Rajasthan Royals"),
        new("RCB", "Royal Challengers Bengaluru"),
        new("SRH", "Sunrisers Hyderabad"),
    ];

    private static readonly Dictionary<string, Franchise> ByCode =
        All.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string? code, out Franchise franchise)
    {
        franchise = null!;
        return code is not null && ByCode.TryGetValue(code, out franchise!);
    }

    public static string NameOf(string code) => TryGet(code, out var f) ? f.Name : code;
}
