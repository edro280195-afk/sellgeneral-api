using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Vista de TIENDA para la compradora (app Flutter, Fase 2). Solo
/// requiere JWT (sub=AccountId); NO exige membership ni negocio activo.
/// Scoping: el `businessId` viene del path y se valida que la compradora
/// tenga un Client reclamado en esa tienda. Cross-tenant en el sentido
/// de que una Account puede tener Client en muchos Business.
/// </summary>
[ApiController]
[Route("api/me/store")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
[SkipTenantResolution]
public class BuyerStoreController : ControllerBase
{
    private readonly IBuyerStoreService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerStoreController(
        IBuyerStoreService service,
        ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// GET /api/me/store/{businessId} — vista pública de una tienda para
    /// la compradora: header, puntos, live (si hay), productos activos
    /// y counts de actividad. Devuelve 404 si la tienda no existe o si
    /// la compradora no tiene un Client reclamado en ella.
    /// </summary>
    [HttpGet("{businessId:int}")]
    public async Task<ActionResult<BuyerStoreDetailDto>> Get(
        int businessId,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var store = await _service.GetStoreAsync(
                _currentAccount.AccountId.Value, businessId, cancellationToken);
            return Ok(store);
        }
        catch (StoreNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
