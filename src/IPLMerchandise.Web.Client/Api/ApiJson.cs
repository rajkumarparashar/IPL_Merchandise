using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPLMerchandise.Web.Client.Api;

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
