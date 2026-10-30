using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Common;
using IPLMerchandise.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace IPLMerchandise.Application.Identity;

public sealed class AuthService(
    IUserRepository users,
    IPasswordHasher hasher,
    ITokenService tokens,
    TimeProvider clock,
    ILogger<AuthService> logger)
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        ValidatePassword(request.Password);
        var user = new User
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = request.Email.Trim(),
            NormalizedEmail = User.Normalize(request.Email),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = hasher.Hash(request.Password),
            Roles = [Roles.Customer],
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };

        if (!await users.TryCreateAsync(user, ct))
            throw new ConflictException(ErrorCodes.EmailInUse, "An account with this email already exists.");

        logger.LogInformation("User {UserId} registered", user.Id);
        return Issue(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(User.Normalize(request.Email), ct);
        // Same error for unknown email and wrong password, so accounts cannot be enumerated.
        if (user is null || !hasher.Verify(user.PasswordHash, request.Password))
        {
            logger.LogInformation("Failed login attempt");
            throw new AuthenticationFailedException();
        }

        logger.LogInformation("User {UserId} logged in", user.Id);
        return Issue(user);
    }

    internal static void ValidatePassword(string password)
    {
        if (password.Length < 8 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw RequestValidationException.For("password", "Password must be at least 8 characters and contain a letter and a digit.");
    }

    private AuthResponse Issue(User user)
    {
        var (token, expires) = tokens.Issue(user);
        return new AuthResponse(token, expires, new UserDto(user.Id, user.Email, user.DisplayName, user.Roles));
    }
}
