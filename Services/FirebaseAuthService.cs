using FirebaseAdmin.Auth;

namespace EntregasApi.Services;

/// <summary>
/// Identidad que Firebase validó criptográficamente. El backend usa estos datos
/// únicamente para resolver la cuenta local y emitir su propia sesión.
/// </summary>
public sealed record FirebaseAuthIdentity(
    string Uid,
    string? PhoneNumber,
    bool IsDisabled);

/// <summary>Valida Firebase ID tokens sin exponer el SDK al controlador.</summary>
public interface IFirebaseAuthService
{
    bool IsConfigured { get; }

    Task<FirebaseAuthIdentity?> VerifyIdTokenAsync(
        string idToken,
        CancellationToken cancellationToken = default);

    /// <summary>Elimina la identidad remota después de confirmar la baja local.</summary>
    Task<bool> DeleteUserAsync(
        string uid,
        CancellationToken cancellationToken = default);
}

/// <summary>Implementación basada en Firebase Admin SDK para .NET.</summary>
public sealed class FirebaseAuthService(
    FirebaseAuth firebaseAuth,
    ILogger<FirebaseAuthService> logger) : IFirebaseAuthService
{
    public bool IsConfigured => true;

    public async Task<FirebaseAuthIdentity?> VerifyIdTokenAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        try
        {
            // VerifyIdTokenAsync valida firma, issuer, audience, expiración y
            // estructura. Nunca se decodifica el JWT manualmente.
            var decodedToken = await firebaseAuth.VerifyIdTokenAsync(
                idToken.Trim(),
                checkRevoked: false,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(decodedToken.Uid)) return null;

            // La consulta adicional permite respetar el estado Disabled de
            // Firebase, que no debe convertirse en acceso local válido.
            var user = await firebaseAuth.GetUserAsync(
                decodedToken.Uid,
                cancellationToken);

            return new FirebaseAuthIdentity(
                decodedToken.Uid,
                user.PhoneNumber ?? GetPhoneClaim(decodedToken),
                user.Disabled);
        }
        catch (FirebaseAuthException ex)
        {
            logger.LogWarning(
                "Firebase rechazó un ID token durante el intercambio de sesión: {ErrorCode}",
                ex.AuthErrorCode);
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(
                ex,
                "Firebase Admin no está disponible para validar sesiones.");
            return null;
        }
    }

    public async Task<bool> DeleteUserAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uid)) return true;

        try
        {
            await firebaseAuth.DeleteUserAsync(uid.Trim(), cancellationToken);
            return true;
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            // La identidad ya no existe: el resultado requerido ya se cumplió.
            return true;
        }
        catch (FirebaseAuthException ex)
        {
            logger.LogError(
                ex,
                "Firebase no pudo eliminar la identidad durante la baja de cuenta: {ErrorCode}",
                ex.AuthErrorCode);
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Firebase Admin no está disponible para eliminar una identidad.");
            return false;
        }
    }

    private static string? GetPhoneClaim(FirebaseToken token)
    {
        return token.Claims.TryGetValue("phone_number", out var rawPhone)
            ? rawPhone as string
            : null;
    }
}

/// <summary>Implementación segura cuando Firebase Admin no tiene credenciales.</summary>
public sealed class UnavailableFirebaseAuthService : IFirebaseAuthService
{
    public bool IsConfigured => false;

    public Task<FirebaseAuthIdentity?> VerifyIdTokenAsync(
        string idToken,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<FirebaseAuthIdentity?>(null);

    public Task<bool> DeleteUserAsync(
        string uid,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
