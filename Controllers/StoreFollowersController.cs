using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Lado vendedora: lista de seguidoras de la tienda activa (para la
/// pantalla de gestión VIP). Tenant-scoped: StoreFollower es ITenantOwned,
/// el query filter automático ya lo acota al negocio activo.
/// </summary>
[ApiController]
[Route("api/business/followers")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
public class StoreFollowersController : ControllerBase
{
    private readonly AppDbContext _db;

    public StoreFollowersController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<StoreFollowerAdminDto>>> GetFollowers(CancellationToken cancellationToken)
    {
        var rows = await _db.StoreFollowers.AsNoTracking()
            .Where(f => f.UnfollowedAt == null)
            .OrderByDescending(f => f.CreatedAt)
            .Join(_db.Accounts.AsNoTracking(),
                f => f.AccountId,
                a => a.Id,
                (f, a) => new StoreFollowerAdminDto(
                    a.Id,
                    string.IsNullOrWhiteSpace(a.DisplayName) ? "Clienta" : a.DisplayName,
                    f.CreatedAt,
                    f.IsVip,
                    f.VipSince))
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }
}
