using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPLMerchandise.Infrastructure.Cosmos;

public static class CosmosJson
{
    /// <summary>Fixed-width ISO-8601 UTC. Cosmos compares strings lexicographically, so the width must never vary.</summary>
    public const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static readonly JsonSerializerOptions Options = Create();

    public static string Format(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(DateFormat, CultureInfo.InvariantCulture);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Cosmos rejects "ttl": null, and omitting nulls saves storage/RUs.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeConverter());
        return options;
    }

    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Format(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value));
    }
}
