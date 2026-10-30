using System.ComponentModel.DataAnnotations;

namespace IPLMerchandise.Contracts.V1;

public sealed record RegisterRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; set; } = "";

    [Required, StringLength(128, MinimumLength = 8)]
    public string Password { get; set; } = "";

    [Required, StringLength(80, MinimumLength = 1)]
    public string DisplayName { get; set; } = "";
}

public sealed record LoginRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; set; } = "";

    [Required, StringLength(128)]
    public string Password { get; set; } = "";
}

public sealed record UserDto(string Id, string Email, string DisplayName, IReadOnlyList<string> Roles);

public sealed record AuthResponse(string AccessToken, DateTime ExpiresAtUtc, UserDto User);
