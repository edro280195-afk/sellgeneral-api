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
        var environmentName = config["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
        if (!isDevelopment &&
            (string.Equals(configuredKey, "dummy", StringComparison.OrdinalIgnoreCase) ||
             keyBytes.Length < 32))
        {
            throw new InvalidOperationException("Jwt:Key debe tener 32+ bytes y no puede ser 'dummy' fuera de Development.");
        }

        if (keyBytes.Length < 32)
        {
            keyBytes = SHA256.HashData(keyBytes);
        }

        return new SymmetricSecurityKey(keyBytes);
    }
}
