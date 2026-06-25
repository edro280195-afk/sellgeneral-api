using EntregasApi.DTOs;

namespace EntregasApi.Services;

/// <summary>
/// Servicio de "reclamar perfil" (plan 0.3). Enlaza una Account global con los
/// Client que las vendedoras ya tienen. Tres caminos, todos con prueba:
///
///  1) <c>ClaimByOrderTokenAsync</c>: la app se abrió desde un link con
///     Order.AccessToken. La posesión del token (recibido por Messenger/SMS al
///     hacer el pedido) ES la prueba. Camino principal.
///
///  2) <c>FindClaimCandidatesByPhoneAsync</c>: tras registrarse, buscamos en
///     TODOS los tenants (IgnoreQueryFilters) los Client con el mismo
///     NormalizedPhone y los ofrecemos uno por uno. No se auto-enlaza.
///
///  3) <c>ClaimByPhoneMatchAsync</c>: la clienta eligió uno del fan-out del
///     punto 2. La prueba es Account.NormalizedPhone == Client.NormalizedPhone.
///     En el futuro esto exigirá OTP; hoy basta la coincidencia normalizada.
///
/// NUNCA se enlaza sin señal de prueba. Cada enlace queda en ClientClaimAudits.
/// </summary>
public interface IClientClaimService
{
    /// <summary>
    /// Enlaza la Account actual con el Client dueño del Order.AccessToken.
    /// Idempotente: si ya está enlazado a la misma Account, devuelve éxito.
    /// </summary>
    Task<ClaimOutcome> ClaimByOrderTokenAsync(int accountId, string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve los Client (cross-tenant) cuyo NormalizedPhone coincide con el
    /// Account.Phone y que AÚN no han sido reclamados por esta Account.
    /// Si la Account no tiene Phone, devuelve lista vacía.
    /// </summary>
    Task<List<ClientClaimCandidateDto>> FindClaimCandidatesByPhoneAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclama un Client específico cuya NormalizedPhone coincide con la
    /// Account.Phone. Devuelve fallo si la Account no tiene Phone o si el
    /// Client no matchea por teléfono.
    /// </summary>
    Task<ClaimOutcome> ClaimByPhoneMatchAsync(int accountId, int clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista los Client que la Account actual ya tiene reclamados (cross-tenant).
    /// No expone datos sensibles: solo identidad + nombre del negocio.
    /// </summary>
    Task<List<ClaimedClientSummaryDto>> ListClaimedClientsAsync(int accountId, CancellationToken cancellationToken = default);
}

public enum ClaimStatus
{
    Linked = 0,        // se enlazó (o ya estaba enlazado a esta Account)
    AlreadyClaimedByOther = 1,  // el Client.AccountId es otra Account
    NoProof = 2,       // falta la señal de prueba
    NotFound = 3,      // no existe el Order / Client
    Forbidden = 4,     // el Account no cumple una pre-condición (ej. no tiene Phone)
}

public record ClaimOutcome(
    ClaimStatus Status,
    ClientClaimResultDto? Result,
    string? Message);
