using System.IdentityModel.Tokens.Jwt;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntregasApi.Tests;

public class AuthTokenTests
{
    [Fact]
    public void GenerateJwt_WithLegacyShortKey_EmitsAccountAndMembershipClaims()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "short",
                ["Jwt:Issuer"] = "tests",
                ["Jwt:Audience"] = "tests"
            })
            .Build();

        var service = new TokenService(config);
        var account = new Account
        {
            Id = 123,
            DisplayName = "Admin Test",
            Email = "admin@example.local"
        };
        var memberships = new[]
        {
            new Membership
            {
                AccountId = account.Id,
                BusinessId = 7,
                Role = MembershipRole.Owner
            }
        };

        var token = service.GenerateJwt(account, memberships);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal("123", jwt.Claims.First(c => c.Type == "sub").Value);
        Assert.Contains(jwt.Claims, c => c.Type == "account_id" && c.Value == "123");
        Assert.Contains(jwt.Claims, c => c.Type == "membership" && c.Value == "7:Owner");
    }
}
