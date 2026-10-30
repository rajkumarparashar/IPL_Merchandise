using IPLMerchandise.Domain.Identity;

namespace IPLMerchandise.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) Issue(User user);
}

/// <summary>The authenticated caller, taken from validated token claims, never from the request body.</summary>
public sealed record Actor(string UserId, string Email)
{
    /// <summary>Value written to audit entries.</summary>
    public string AuditName => $"{Email} ({UserId})";
}
