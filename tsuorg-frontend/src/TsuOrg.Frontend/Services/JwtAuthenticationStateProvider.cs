using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using TsuOrg.Frontend.Models;

namespace TsuOrg.Frontend.Services;

public sealed class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly ITokenStore _tokens;
    private readonly AuthSession _session;

    public JwtAuthenticationStateProvider(ITokenStore tokens, AuthSession session)
    {
        _tokens = tokens;
        _session = session;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var access = await _tokens.GetAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(access) || IsExpired(access))
            {
                _session.Clear();
                return Anonymous();
            }

            var principal = CreatePrincipal(access);
            _session.ApplyFromPrincipal(principal);
            return new AuthenticationState(principal);
        }
        catch
        {
            _session.Clear();
            return Anonymous();
        }
    }

    public async Task SignInAsync(LoginResponse login)
    {
        await _tokens.SaveAsync(login.AccessToken, login.RefreshToken);
        _session.Apply(login);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(CreatePrincipal(login.AccessToken))));
    }

    public async Task SignOutAsync()
    {
        await _tokens.ClearAsync();
        _session.Clear();
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous()));
    }

    private static AuthenticationState Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static ClaimsPrincipal CreatePrincipal(string jwt)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);
        var identity = new ClaimsIdentity(token.Claims, authenticationType: "Bearer",
            nameType: "name", roleType: ClaimTypes.Role);

        // Some tokens emit short "role" / "name" claim types — normalize for [Authorize(Roles=...)].
        if (!identity.HasClaim(c => c.Type == ClaimTypes.Role))
        {
            foreach (var role in token.Claims.Where(c => c.Type is "role" or "roles"))
                identity.AddClaim(new Claim(ClaimTypes.Role, role.Value));
        }

        if (!identity.HasClaim(c => c.Type == ClaimTypes.Name))
        {
            var name = token.Claims.FirstOrDefault(c => c.Type is "name" or JwtRegisteredClaimNames.Name)?.Value;
            if (!string.IsNullOrEmpty(name))
                identity.AddClaim(new Claim(ClaimTypes.Name, name));
        }

        return new ClaimsPrincipal(identity);
    }

    private static bool IsExpired(string jwt)
    {
        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
            return token.ValidTo <= DateTime.UtcNow.AddSeconds(-30);
        }
        catch
        {
            return true;
        }
    }
}
