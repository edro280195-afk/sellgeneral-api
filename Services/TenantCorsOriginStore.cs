using EntregasApi.Data;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

/// <summary>
/// Resuelve qué orígenes acepta CORS en modo multi-tenant: los dominios registrados
/// en cada <c>Business.FrontendUrl</c> (cacheados con TTL corto) más los orígenes
/// fijos de desarrollo y Capacitor. Reemplaza la lista hardcodeada de regibazar.com.
/// Es singleton, así que abre un scope con <see cref="IServiceScopeFactory"/> para
/// consultar la base de datos sin capturar un <c>AppDbContext</c> scoped.
/// </summary>
public class TenantCorsOriginStore
{
    private static readonly string[] StaticOrigins =
    {
        "http://localhost:4200",
        "http://localhost",
        "https://localhost",
        "capacitor://localhost"
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _lock = new();
    private HashSet<string> _tenantOrigins = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedAtUtc = DateTime.MinValue;

    public TenantCorsOriginStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>True si el origen es uno fijo (dev/Capacitor) o el dominio de algún negocio.</summary>
    public bool IsAllowed(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        var normalized = origin.TrimEnd('/');

        if (StaticOrigins.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        EnsureLoaded();
        return _tenantOrigins.Contains(normalized);
    }

    private void EnsureLoaded()
    {
        if (DateTime.UtcNow - _loadedAtUtc < CacheTtl)
        {
            return;
        }

        lock (_lock)
        {
            if (DateTime.UtcNow - _loadedAtUtc < CacheTtl)
            {
                return;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var urls = db.Businesses
                    .AsNoTracking()
                    .Where(b => b.FrontendUrl != null && b.FrontendUrl != "")
                    .Select(b => b.FrontendUrl!)
                    .ToList();

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var url in urls)
                {
                    var origin = url.TrimEnd('/');
                    set.Add(origin);
                    // También aceptamos la variante apex<->www para no regresar el CORS viejo
                    // (que permitía regibazar.com y www.regibazar.com aunque solo se registre uno).
                    var alternate = ToggleWww(origin);
                    if (alternate is not null)
                    {
                        set.Add(alternate);
                    }
                }

                _tenantOrigins = set;
                _loadedAtUtc = DateTime.UtcNow;
            }
            catch
            {
                // Si la base aún no está disponible (p.ej. durante el arranque), no rompas
                // CORS: se siguen aceptando los orígenes fijos y se reintenta en el próximo request.
            }
        }
    }

    /// <summary>Dado "scheme://host[:port]" devuelve la variante con/sin "www." (o null si no aplica).</summary>
    private static string? ToggleWww(string origin)
    {
        var schemeSep = origin.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep < 0)
        {
            return null;
        }

        var scheme = origin[..schemeSep];
        var hostAndPort = origin[(schemeSep + 3)..];

        return hostAndPort.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? $"{scheme}://{hostAndPort[4..]}"
            : $"{scheme}://www.{hostAndPort}";
    }
}
