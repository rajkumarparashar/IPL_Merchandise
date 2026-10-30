namespace IPLMerchandise.Web.Client.Formatting;

/// <summary>Human-friendly order number shown in the UI: "#IPL-" plus the first 8 characters of the order id.</summary>
public static class OrderNumber
{
    public static string Format(Guid id) => "#IPL-" + id.ToString("N")[..8].ToUpperInvariant();
}
