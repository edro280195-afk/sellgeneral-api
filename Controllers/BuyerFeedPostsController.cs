using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Novedades de una tienda para la compradora. Solo requiere JWT
/// (sub=AccountId); NO exige membership ni negocio activo.
/// </summary>
[ApiController]
[Route("api/me/store")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
[SkipTenantResolution]
public class BuyerFeedPostsController : ControllerBase
{
    private readonly IBuyerFeedPostsService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerFeedPostsController(IBuyerFeedPostsService service, ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    [HttpGet("{businessId:int}/posts")]
    public async Task<ActionResult<List<StorePostFeedItemDto>>> GetPosts(
        int businessId,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var posts = await _service.GetStorePostsAsync(
                _currentAccount.AccountId.Value, businessId,
                page == 0 ? 1 : page, pageSize == 0 ? 20 : pageSize,
                cancellationToken);
            return Ok(posts);
        }
        catch (StoreNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
