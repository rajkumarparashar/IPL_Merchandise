using System.Security.Claims;
using System.Text.Json;
using IPLMerchandise.Contracts.V1;
using Microsoft.AspNetCore.Components.Authorization;

namespace IPLMerchandise.Web.Client.Auth;

/// <summary>
/// Builds the UI's view of the user from the stored JWT. Used only to decide what to show: every real authorisation
/// decision is made by the API, which validates the token's signature.
/// </summary>
public sealed class JwtAuthStateProvider(TokenStore tokens, TimeProvider clock) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await tokens.GetAsync();
        if (token is null) return Anonymous;

        var claims = ParseClaims(token);
        var exp = claims.FirstOrDefault(c => c.Type == "exp")?.Value;
        if (claims.Count == 0 || exp is null || DateTimeOffset.FromUnixTimeSeconds(long.Parse(exp)) <= clock.GetUtcNow())
        {
            await tokens.SetAsync(null);
            return Anonymous;
        }
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt", "name", "role")));
    }

    public async Task SignInAsync(AuthResponse auth)
    {
        await tokens.SetAsync(auth.AccessToken);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task SignOutAsync()
    {
        await tokens.SetAsync(null);
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }

    internal static List<Claim> ParseClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return [];
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var claims = new List<Claim>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Array)
                    claims.AddRange(p.Value.EnumerateArray().Select(v => new Claim(p.Name, v.ToString())));
                else
                    claims.Add(new Claim(p.Name, p.Value.ToString()));
            }
            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return [];
        }
    }
}
