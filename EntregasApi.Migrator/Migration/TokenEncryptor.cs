using Microsoft.AspNetCore.DataProtection;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Encripta el token MP de la vendedora con el mismo protector que usa la app
/// (DataProtection con purpose "EntregasApi.Business.MercadoPagoAccessToken"
/// y ApplicationName "EntregasApi"). Asi el valor queda encriptado y la app
/// lo des-encripta sin necesidad de reescribir nada.
/// </summary>
public sealed class TokenEncryptor : IDisposable
{
    private const string ApplicationName = "EntregasApi";
    private const string ProtectorPurpose = "EntregasApi.Business.MercadoPagoAccessToken";

    private readonly IDataProtector _protector;

    public TokenEncryptor()
    {
        // Mismo nombre de aplicacion que la web app: clave del esquema de DataProtection.
        var provider = DataProtectionProvider.Create(ApplicationName);
        _protector = provider.CreateProtector(ProtectorPurpose);
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return plaintext;
        }
        return _protector.Protect(plaintext);
    }

    public void Dispose()
    {
        // IDataProtector no implementa IDisposable; nada que liberar.
    }
}
