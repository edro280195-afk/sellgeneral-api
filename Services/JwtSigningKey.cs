using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EntregasApi.Services;

public static class JwtSigningKey
{
    public static SymmetricSecurityKey FromConfiguration(IConfiguration config)
    {
        var configuredKey = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            throw new InvalidOperationException("Jwt:Key no esta configurado.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(configuredKey);
        if (keyBytes.Length < 32)
        {
            keyBytes = SHA256.HashData(keyBytes);
        }

        return new SymmetricSecurityKey(keyBytes);
    }
}
