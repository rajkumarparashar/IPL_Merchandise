namespace IPLMerchandise.Domain.Identity;

public static class Roles
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
}

public sealed class User
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string NormalizedEmail { get; init; }
    public required string DisplayName { get; init; }
    public required string PasswordHash { get; set; }
    public required IReadOnlyList<string> Roles { get; init; }
    public required DateTime CreatedAtUtc { get; init; }

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();
}
