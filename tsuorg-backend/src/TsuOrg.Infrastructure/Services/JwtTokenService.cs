using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using TsuOrg.Application.Common;

namespace TsuOrg.Infrastructure.Services;

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly string _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int    _accessMinutes;

    public JwtTokenService(IConfiguration config)
    {
        _key           = config["Jwt:Key"]          ?? "DEV_ONLY_CHANGE_ME_TSUORG_SUPER_SECRET_KEY_32+";
        _issuer        = config["Jwt:Issuer"]       ?? "tsuorg";
        _audience      = config["Jwt:Audience"]     ?? "tsuorg";
        _accessMinutes = int.TryParse(config["Jwt:AccessTokenMinutes"], out var m) ? m : 60;
    }

    public string GenerateAccessToken(Guid userId, string email, string role, string fullName)
    {
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,   userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Name,  fullName),
            new(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier,     userId.ToString()),
            new(ClaimTypes.Role,               role),
            new(ClaimTypes.Email,              email),
        };

        var token = new JwtSecurityToken(
            issuer:             _issuer,
            audience:           _audience,
            claims:             claims,
            notBefore:          DateTime.UtcNow,
            expires:            DateTime.UtcNow.AddMinutes(_accessMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public bool ValidateRefreshToken(string token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length >= 64;
}
