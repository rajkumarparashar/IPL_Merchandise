using System.ComponentModel.DataAnnotations;

namespace IPLMerchandise.API.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    [Required] public string Issuer { get; set; } = "";
    [Required] public string Audience { get; set; } = "";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Supplied by environment / Key Vault; the API refuses to start without it.</summary>
    [Required, MinLength(32)] public string SigningKey { get; set; } = "";

    [Range(5, 24 * 60)] public int AccessTokenMinutes { get; set; } = 60;
}
