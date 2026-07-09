using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Endpoints de SORTEOS de la compradora (app Flutter, Fase 2). Solo
/// requieren JWT (sub=AccountId); NO exigen membership ni negocio activo.
/// Sigue el patrón cross-tenant por AccountId con IgnoreQueryFilters.
/// </summary>
[ApiController]
[Route("api/me/raffles")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
[SkipTenantResolution]
public class BuyerRafflesController : ControllerBase
{
    private readonly IBuyerRafflesService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerRafflesController(
        IBuyerRafflesService service,
        ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// GET /api/me/raffles — sorteos de las tiendas donde la compradora
    /// está reclamada (cross-tenant por AccountId). Excluye los sorteos
    /// en estado "Draft" (aún no publicados). Enriquce cada uno con
    /// `MyEntryCount`, `IsMineEntered` y `AmIWinner` para que la app
    /// pueda distinguir "Activos", "Mis boletos" e "Historial".
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MyRaffleDto>>> List(
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var result = await _service.GetMyRafflesAsync(
            _currentAccount.AccountId.Value, cancellationToken);
        return Ok(result);
    }
}
