using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Endpoints de la COMPRADORA (app Flutter, Fase 2). Solo requieren un JWT
/// válido (sub=AccountId); NO exigen membership ni negocio activo, porque la
/// compradora puede no ser dueña de ningún negocio.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
[SkipTenantResolution]
public class BuyerController : ControllerBase
{
    private readonly IBuyerFeedService _feed;
    private readonly IBuyerOrdersService _orders;
    private readonly IBuyerRewardsService _rewards;
    private readonly ICurrentAccount _currentAccount;

    public BuyerController(
        IBuyerFeedService feed,
        IBuyerOrdersService orders,
        IBuyerRewardsService rewards,
        ICurrentAccount currentAccount)
    {
        _feed = feed;
        _orders = orders;
        _rewards = rewards;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// GET /api/me/home — feed de inicio: tiendas reclamadas (con marca + puntos),
    /// total de puntos, pedido activo y pedidos recientes (cross-tenant por AccountId).
    /// </summary>
    [HttpGet("home")]
    public async Task<ActionResult<BuyerHomeDto>> Home(CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var home = await _feed.GetHomeAsync(_currentAccount.AccountId.Value, cancellationToken);
        return Ok(home);
    }

    /// <summary>
    /// GET /api/me/orders — pedidos de la compradora (cross-tenant por AccountId),
    /// paginados y filtrados por estado. Filtros: all | open | closed. businessId
    /// opcional para acotar a una tienda. page 1-based, pageSize máx 50.
    /// </summary>
    [HttpGet("orders")]
    public async Task<ActionResult<BuyerOrdersResponse>> Orders(
        [FromQuery] string? filter,
        [FromQuery] int? businessId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        if (!BuyerOrderFilter.IsValid(filter))
        {
            return BadRequest(new { message = "Filtro inválido. Usa 'all', 'open' o 'closed'." });
        }

        var response = await _orders.GetOrdersAsync(
            _currentAccount.AccountId.Value,
            filter ?? BuyerOrderFilter.All,
            businessId,
            page,
            pageSize,
            cancellationToken);

        return Ok(response);
    }

    /// <summary>
    /// GET /api/me/rewards — catálogo de premios canjeables de las tiendas
    /// donde la compradora está reclamada (cross-tenant por AccountId), junto
    /// con los puntos que tiene acumulados en cada una. Tiendas sin premios
    /// activos aparecen con `rewards` vacío.
    /// </summary>
    [HttpGet("rewards")]
    public async Task<ActionResult<List<RewardsByBusinessDto>>> Rewards(
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var rewards = await _rewards.GetRewardsAsync(
            _currentAccount.AccountId.Value, cancellationToken);
        return Ok(rewards);
    }
}
