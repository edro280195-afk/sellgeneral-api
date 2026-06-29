using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Acciones de la compradora sobre pedidos (apartar, etc.). Solo
/// requieren JWT (sub=AccountId); NO exigen membership ni negocio
/// activo. Scoping: el `businessId` viene del body y se valida que la
/// compradora tenga un Client reclamado en esa tienda.
/// </summary>
[ApiController]
[Route("api/me/reserve")]
[Authorize]
public class BuyerReserveController : ControllerBase
{
    private readonly IBuyerReserveService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerReserveController(
        IBuyerReserveService service,
        ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// POST /api/me/reserve — crea un Order con `Status = Pending` y
    /// `OrderType = PickUp` para apartar un producto. Devuelve 404 si la
    /// tienda o el producto no existen, 400 si está inactivo o sin stock.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<BuyerOrderDto>> Reserve(
        [FromBody] ReserveProductRequest request,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var order = await _service.ReserveAsync(
                _currentAccount.AccountId.Value, request, cancellationToken);
            return Ok(order);
        }
        catch (ReserveNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ReserveBadRequestException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
