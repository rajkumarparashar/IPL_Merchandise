using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.Application.Common;

/// <summary>
/// Opaque keyset cursor: base64url(JSON { f: filter fingerprint, c: last key seen }).
/// The fingerprint ties a token to the filters that produced it, so a token cannot be replayed against a different query.
/// Tokens are not secret: a tampered cursor only moves the start position of the caller's own, already-authorised query.
/// </summary>
public static class ContinuationToken
{
    private sealed record Envelope<T>(string F, T C);

    public static string Encode<T>(T cursor, string fingerprint) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(fingerprint, cursor)));

    public static T? Decode<T>(string? token, string fingerprint) where T : class
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            if (token.Length > 1024) throw new FormatException();
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(Base64Url.DecodeFromChars(token));
            if (envelope?.C is null || envelope.F != fingerprint) throw new FormatException();
            return envelope.C;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw RequestValidationException.For("continuationToken",
                "The continuation token is invalid or was issued for different filters.", ErrorCodes.InvalidContinuationToken);
        }
    }

    public static string Fingerprint(params object?[] parts)
    {
        var raw = string.Join('|', parts.Select(p => p?.ToString()?.ToLowerInvariant() ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }
}
