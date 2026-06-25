using System.Text.RegularExpressions;

namespace EntregasApi.Services;

/// <summary>
/// Convención central de nombres de grupos de SignalR (plan 0.4).
///
/// Reglas:
/// - TODO nombre de grupo se prefija con <c>"t{BusinessId}_"</c> para que un evento
///   de un tenant jamás llegue a conexiones de otro.
/// - Sufijos canónicos (sin prefijar):
///     Admins, PosNodriza, Route_{driverToken}, Order_{accessToken},
///     Tracking_{driverToken}, PosOrder_{orderId}.
/// - Los recursos (tokens, IDs) se sanitizan para que un valor hostil no
///   pueda inyectar separadores ni saltos de grupo.
///
/// Importante: cualquier <c>Clients.Group(...)</c> o
/// <c>Groups.AddToGroupAsync(...)</c> del backend DEBE pasar por aquí.
/// </summary>
public static class SignalRGroupNames
{
    /// <summary>Caracteres permitidos en tokens que van dentro del nombre del grupo.</summary>
    private static readonly Regex TokenAllowed = new(@"^[A-Za-z0-9_\-]+$", RegexOptions.Compiled);

    /// <summary>Prefijo de tenant para un nombre de grupo.</summary>
    public static string TenantPrefix(int businessId) => $"t{businessId}_";

    // ── Grupos administrativos ──

    /// <summary>Grupo de admins del tenant (panel principal).</summary>
    public static string Admins(int businessId) => $"{TenantPrefix(businessId)}Admins";

    /// <summary>Grupo de la "Nodriza" del POS (todas las cajas del tenant).</summary>
    public static string PosNodriza(int businessId) => $"{TenantPrefix(businessId)}PosNodriza";

    // ── Grupos de ruta (driver) ──

    /// <summary>
    /// Grupo de la ruta de un chofer, identificado por DriverToken (string único).
    /// El DeliveryHub y el TrackingHub lo consumen; la app del chofer y el panel
    /// de logística se unen aquí.
    /// </summary>
    public static string Route(int businessId, string driverToken)
    {
        EnsureSafeToken(driverToken, nameof(driverToken));
        return $"{TenantPrefix(businessId)}Route_{driverToken}";
    }

    /// <summary>
    /// Grupo público de rastreo: la clienta y la vista pública se unen aquí para
    /// recibir <c>LocationUpdate</c>. NO debe confundirse con Route_ (que es interno).
    /// </summary>
    public static string Tracking(int businessId, string driverToken)
    {
        EnsureSafeToken(driverToken, nameof(driverToken));
        return $"{TenantPrefix(businessId)}Tracking_{driverToken}";
    }

    // ── Grupos de pedido ──

    /// <summary>
    /// Grupo del pedido de la clienta (vista Order_/order_ unificada), identificado
    /// por Order.AccessToken. La clienta y Cami se unen aquí.
    /// </summary>
    public static string Order(int businessId, string accessToken)
    {
        EnsureSafeToken(accessToken, nameof(accessToken));
        return $"{TenantPrefix(businessId)}Order_{accessToken}";
    }

    /// <summary>
    /// Grupo del pedido dentro del POS, identificado por el int orderId. Va prefijado
    /// por tenant para que dos tenants con el mismo orderId no colisionen.
    /// </summary>
    public static string PosOrder(int businessId, int orderId)
    {
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId), "orderId debe ser positivo.");
        return $"{TenantPrefix(businessId)}PosOrder_{orderId}";
    }

    // ── Helpers ──

    /// <summary>
    /// Garantiza que un token (AccessToken, DriverToken) cumple el whitelist
    /// antes de meterlo en el nombre del grupo. Defensa contra inyecciones
    /// del cliente que intenten saltar de grupo con separadores o guiones raros.
    /// </summary>
    public static void EnsureSafeToken(string token, string paramName)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token vacío.", paramName);
        if (!TokenAllowed.IsMatch(token))
            throw new ArgumentException(
                $"El token contiene caracteres no permitidos para un nombre de grupo (solo A-Z, a-z, 0-9, _ y -).",
                paramName);
    }
}
