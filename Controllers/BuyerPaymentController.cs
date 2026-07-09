using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Endpoints de PAGOS de la compradora (app Flutter, Fase 2). Solo
/// requieren JWT (sub=AccountId); NO exigen membership ni negocio activo.
/// Scoping cross-tenant por AccountId.
/// </summary>
[ApiController]
[Route("api/me/payments")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
[SkipTenantResolution]
public class BuyerPaymentController : ControllerBase
{
    private readonly IBuyerPaymentService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerPaymentController(
        IBuyerPaymentService service,
        ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// GET /api/me/payments — historial de pagos de la compradora,
    /// ordenado por fecha descendente. Incluye info del Order y la
    /// tienda para que la app agrupe sin N+1 requests.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<BuyerPaymentDto>>> List(
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var payments = await _service.GetMyPaymentsAsync(
            _currentAccount.AccountId.Value, cancellationToken);
        return Ok(payments);
    }
}
