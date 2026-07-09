using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Endpoints de TANDAS de la compradora (app Flutter, Fase 2). Solo
/// requieren JWT (sub=AccountId); NO exigen membership ni negocio activo.
/// Sigue el patrón cross-tenant por AccountId con IgnoreQueryFilters.
/// </summary>
[ApiController]
[Route("api/me/tandas")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
[SkipTenantResolution]
public class BuyerTandasController : ControllerBase
{
    private readonly IBuyerTandasService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerTandasController(
        IBuyerTandasService service,
        ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// GET /api/me/tandas — tandas de las tiendas donde la compradora está
    /// reclamada (cross-tenant por AccountId). Marca `IsMine` y enriquece
    /// con su turno, semanas pagadas y si gana esta semana. Las tandas
    /// "Disponibles" (donde aún no está inscrita) también vienen en la
    /// lista con `IsMine = false`.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MyTandaDto>>> List(
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var result = await _service.GetMyTandasAsync(
            _currentAccount.AccountId.Value, cancellationToken);
        return Ok(result);
    }
}
