using System.Security.Claims;
using IPLMerchandise.Application.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;

namespace IPLMerchandise.API.Security;

public static class AuthConstants
{
    public const string RoleClaim = "role";
    public const string CustomerPolicy = "Customer";
    public const string AdminPolicy = "Admin";
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The caller's id from the validated token. Ownership decisions use only this, never ids sent in the request.</summary>
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? throw new InvalidOperationException("Authenticated user has no 'sub' claim.");

    public static Actor ToActor(this ClaimsPrincipal user) =>
        new(user.GetUserId(), user.FindFirstValue(JwtRegisteredClaimNames.Email) ?? "unknown");
}
