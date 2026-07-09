using System.ComponentModel.DataAnnotations;
using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Analítica auto-alojada de enlaces de pedido. La landing pública
/// (<c>/o/{accessToken}</c>) y la app (tras capturar el Install Referrer)
/// disparan eventos aquí: <c>impression</c>, <c>open_app</c>,
/// <c>store_android</c>, <c>store_ios</c>, <c>install_referrer</c>.
///
/// El endpoint es anónimo (no requiere auth): el <c>accessToken</c> del
/// pedido es el propio "secreto" del enlace. El <c>BusinessId</c> se
/// resuelve desde el pedido para que el dueño del tenant vea sólo sus
/// eventos (query filter de <see cref="ITenantOwned"/>).
/// </summary>
[ApiController]
[AllowAnonymous]
public class LinkEventsController : ControllerBase
{
    private readonly AppDbContext _db;

    public LinkEventsController(AppDbContext db) => _db = db;

    /// <summary>POST /api/link-events — registra un evento de analítica.</summary>
    [HttpPost("/api/link-events")]
    [EnableRateLimiting(SecurityRateLimitPolicies.LinkEvents)]
    public async Task<IActionResult> Record([FromBody] RecordLinkEventDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        // Resolvemos el BusinessId desde el pedido para que el evento quede
        // asociado al tenant correcto (el query filter filtra por tenant en
        // lecturas admin; los inserts no se filtran).
        var businessId = await _db.Orders.AsNoTracking()
            .Where(o => o.AccessToken == dto.AccessToken)
            .Select(o => (int?)o.BusinessId)
            .FirstOrDefaultAsync();

        if (businessId is null)
        {
            return Accepted(new { ok = false, reason = "invalid_token" });
        }

        // Si el token no corresponde a ningún pedido, lo registramos igual
        // con el default (Business #1) para no perder el evento; el dueño
        // del tenant default verá los huérfanos.
        var ev = new LinkEvent
        {
            BusinessId = businessId.Value,
            OrderAccessToken = dto.AccessToken,
            Event = dto.Event,
            Referrer = dto.Referrer,
            UserAgent = dto.UserAgent ?? ExtractUserAgent(),
            IpAddress = ExtractIp(),
        };

        _db.LinkEvents.Add(ev);
        await _db.SaveChangesAsync();

        // 202: el evento se registró pero no hay un "recurso" que devolver.
        return Accepted(new { ok = true, id = ev.Id });
    }

    private string? ExtractUserAgent()
    {
        var ua = Request.Headers["User-Agent"].ToString();
        return string.IsNullOrWhiteSpace(ua) ? null : Truncate(ua, 512);
    }

    private string? ExtractIp()
    {
        var remote = HttpContext.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrWhiteSpace(remote) ? null : Truncate(remote, 64);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);
}

/// <summary>Cuerpo del POST /api/link-events.</summary>
public record RecordLinkEventDto(
    [Required] [MaxLength(80)] string AccessToken,
    [Required] [MaxLength(32)] string Event,
    [MaxLength(512)] string? Referrer = null,
    [MaxLength(512)] string? UserAgent = null);
