using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Endpoints de DIRECCIONES de la compradora (app Flutter, Fase 2).
/// Solo requieren JWT (sub=AccountId); NO exigen membership. Scoping
/// cross-tenant: el clientId del path debe pertenecer a un Client con
/// AccountId == sub del JWT.
/// </summary>
[ApiController]
[Route("api/me/addresses")]
[Authorize]
public class BuyerAddressController : ControllerBase
{
    private readonly IBuyerAddressService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerAddressController(
        IBuyerAddressService service,
        ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// GET /api/me/addresses — lista de direcciones de la compradora
    /// (un registro por Client reclamado, agrupado por tienda).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<BuyerAddressDto>>> List(
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var addresses = await _service.GetMyAddressesAsync(
            _currentAccount.AccountId.Value, cancellationToken);
        return Ok(addresses);
    }

    /// <summary>
    /// PUT /api/me/addresses/{clientId} — actualiza la dirección del
    /// Client indicado. Solo se modifican los campos no-null en el body.
    /// Devuelve 404 si el Client no pertenece a la Account.
    /// </summary>
    [HttpPut("{clientId:int}")]
    public async Task<ActionResult<BuyerAddressDto>> Update(
        int clientId,
        [FromBody] UpdateBuyerAddressRequest request,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var address = await _service.UpdateAddressAsync(
                _currentAccount.AccountId.Value, clientId, request, cancellationToken);
            return Ok(address);
        }
        catch (AddressNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
