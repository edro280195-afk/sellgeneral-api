using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using EntregasApi.Models;
using Microsoft.IdentityModel.Tokens;

namespace EntregasApi.Services;

public interface ITokenService
{
    string GenerateJwt(Account account, IEnumerable<Membership> memberships);
    string GenerateAccessToken();
}

public class TokenService : ITokenService
{
    private readonly IConfiguration _config;

    public TokenService(IConfiguration config)
    {
        _config = config;
    }

    public string GenerateJwt(Account account, IEnumerable<Membership> memberships)
    {
        var key = JwtSigningKey.FromConfiguration(_config);
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var accountId = account.Id.ToString();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, accountId),
            new(ClaimTypes.NameIdentifier, accountId),
            new("account_id", accountId),
            new(ClaimTypes.Name, account.DisplayName)
        };

        if (!string.IsNullOrWhiteSpace(account.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, account.Email));
        }

        foreach (var membership in memberships)
        {
            claims.Add(new Claim("membership", $"{membership.BusinessId}:{membership.Role}"));
        }

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateAccessToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }
}
