using IPLMerchandise.Application.Abstractions;
using Microsoft.AspNetCore.Identity;

namespace IPLMerchandise.Infrastructure.Security;

/// <summary>
/// Adapter over ASP.NET Core Identity's hasher (PBKDF2-HMAC-SHA512, 100k iterations, per-user salt, versioned format).
/// Using the framework implementation avoids hand-rolled crypto and keeps hashes compatible with full Identity later.
/// </summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object User = new();

    public string Hash(string password) => Hasher.HashPassword(User, password);

    public bool Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(User, hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}
