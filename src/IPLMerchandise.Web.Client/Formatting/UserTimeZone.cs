using Microsoft.JSInterop;

namespace IPLMerchandise.Web.Client.Formatting;

/// <summary>
/// Timestamps are stored and sent in UTC; this converts them for display. Default is IST (Asia/Kolkata);
/// the user can switch to UTC or their browser's zone, and the choice is remembered.
/// </summary>
public sealed class UserTimeZone(IJSRuntime js)
{
    public const string Ist = "Asia/Kolkata";
    public const string Utc = "UTC";
    public const string Browser = "browser";
    private const string StorageKey = "ipl.timeZone";

    public static readonly IReadOnlyList<(string Id, string Label)> Choices =
        [(Ist, "India Standard Time (IST)"), (Utc, "UTC"), (Browser, "My device's time zone")];

    public string Selected { get; private set; } = Ist;
    public event Action? Changed;

    public TimeZoneInfo Zone => Resolve(Selected);

    public async Task LoadAsync()
    {
        try
        {
            var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (stored is not null && Choices.Any(c => c.Id == stored)) Selected = stored;
        }
        catch (JSException) { }
    }

    public async Task SetAsync(string id)
    {
        Selected = Choices.Any(c => c.Id == id) ? id : Ist;
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, Selected); } catch (JSException) { }
        Changed?.Invoke();
    }

    public string Format(DateTime utc) => FormatIn(utc, Zone);

    public static string FormatIn(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return $"{local.ToString("dd MMM yyyy, hh:mm tt", System.Globalization.CultureInfo.InvariantCulture)} {Abbreviation(zone)}";
    }

    /// <summary>Start of a calendar day in the selected zone, as an absolute instant for the API.</summary>
    public DateTimeOffset StartOfDay(DateOnly date) => StartOfDayIn(date, Zone);

    public static DateTimeOffset StartOfDayIn(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static TimeZoneInfo Resolve(string id)
    {
        if (id == Browser) return TimeZoneInfo.Local;
        if (id == Utc) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return IstFallback; } // invariant-timezone builds: IST has no DST, so a fixed offset is exact
    }

    private static readonly TimeZoneInfo IstFallback = TimeZoneInfo.CreateCustomTimeZone(Ist, TimeSpan.FromHours(5.5), "India Standard Time", "IST");

    private static string Abbreviation(TimeZoneInfo zone) => zone.Id switch
    {
        Ist or "India Standard Time" => "IST",
        "UTC" or "Etc/UTC" => "UTC",
        _ => zone.BaseUtcOffset == TimeSpan.Zero ? "UTC" : $"UTC{(zone.BaseUtcOffset < TimeSpan.Zero ? "-" : "+")}{zone.BaseUtcOffset:hh\\:mm}"
    };
}
