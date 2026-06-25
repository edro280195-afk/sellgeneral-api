using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

/// <summary>
/// Carga (y cachea por request) la entidad <see cref="Business"/> del tenant activo.
/// Es la pieza que permite de-hardcodear identidad/depot/región/slug: los servicios
/// leen estos valores desde el Business en lugar de constantes globales.
/// El BusinessId activo lo resuelve <see cref="ICurrentTenant"/> (middleware de auth
/// con X-Business-Id, o token de recurso para vistas públicas).
/// </summary>
public interface ICurrentBusiness
{
    /// <summary>
    /// Negocio activo (cargado de forma perezosa y cacheado para el resto del request).
    /// Úsalo en código síncrono. Lanza si el tenant activo no existe en la base.
    /// </summary>
    Business Current { get; }

    /// <summary>Versión asíncrona; cachea el resultado igual que <see cref="Current"/>.</summary>
    Task<Business> GetAsync(CancellationToken cancellationToken = default);
}

public class CurrentBusiness : ICurrentBusiness
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private Business? _cached;

    public CurrentBusiness(AppDbContext db, ICurrentTenant tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public Business Current
    {
        get
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var businessId = _tenant.ActiveBusinessId;
            _cached = _db.Businesses.AsNoTracking().FirstOrDefault(b => b.Id == businessId)
                      ?? throw new InvalidOperationException(
                          $"No existe el negocio activo (BusinessId={businessId}).");
            return _cached;
        }
    }

    public async Task<Business> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var businessId = _tenant.ActiveBusinessId;
        _cached = await _db.Businesses.AsNoTracking()
                      .FirstOrDefaultAsync(b => b.Id == businessId, cancellationToken)
                  ?? throw new InvalidOperationException(
                      $"No existe el negocio activo (BusinessId={businessId}).");
        return _cached;
    }
}
