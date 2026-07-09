using System.Globalization;
using System.Net;
using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Dominio compartido de enlaces de pedido (p. ej. https://sellgeneral.app).
/// Sirve:
///  - <c>GET /o/{token}</c>: el "muro de instalación" (HTML) que orilla a bajar
///    la app. Si la app está instalada y el App Link verificado, el SO abre la
///    app antes de llegar aquí; si no, se muestra el teaser + descarga.
///  - <c>GET /api/pedido/{token}/teaser</c>: DTO slim público para vistas previas.
///  - los archivos de verificación de App Links (Android) y Universal Links (iOS).
///
/// Nada aquí expone datos sensibles: sólo el teaser (tienda, nombre, total,
/// estatus), tan público como el enlace mismo.
/// </summary>
[ApiController]
[AllowAnonymous]
public class ShareLandingController : ControllerBase
{
    private const string AndroidPackage = "com.nenisapp.nenis_app";

    // SHA-256 del debug.keystore local: permite verificar App Links en builds
    // debug. En producción, añade la huella de Play App Signing vía la config
    // App:AndroidCertFingerprints (en Render: env App__AndroidCertFingerprints,
    // varias separadas por coma).
    private const string DefaultDebugFingerprint =
        "59:57:3A:42:52:47:25:DC:B7:1D:00:71:49:73:2F:BC:7B:73:13:7D:BC:CA:DB:DF:5A:A1:2C:83:02:C6:33:E6";

    // Universal Links iOS. Sustituir TEAMID por el Apple Team ID vía la config
    // App:AppleAppId al tener la cuenta de Apple Developer.
    private const string DefaultAppleAppId = "TEAMID.com.nenisapp.nenis_app";

    private static readonly CultureInfo EsMx = new("es-MX");

    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public ShareLandingController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    /// <summary>GET /o/{accessToken} — muro de instalación (HTML). El parámetro
    /// se llama <c>accessToken</c> para que TenantResolutionMiddleware resuelva
    /// el negocio del pedido y el filtro global multi-tenant lo encuentre.</summary>
    [HttpGet("/o/{accessToken}")]
    [EnableRateLimiting(SecurityRateLimitPolicies.PublicTokenRead)]
    public async Task<IActionResult> Landing(string accessToken)
    {
        var order = await LoadOrderAsync(accessToken);

        string? businessName = null;
        string? businessLogo = null;
        if (order != null)
        {
            var biz = await _db.Businesses.AsNoTracking()
                .Where(b => b.Id == order.BusinessId)
                .Select(b => new { b.Name, b.LogoUrl })
                .FirstOrDefaultAsync();
            businessName = biz?.Name;
            businessLogo = biz?.LogoUrl;
        }

        // Registramos la impresión server-side (jamás se pierde aunque el JS
        // del muro no cargue). El click tracking (open_app / store_*) lo hace
        // el JS del muro con un POST a /api/link-events.
        await RecordImpressionAsync(accessToken, order?.BusinessId);

        var html = BuildLandingHtml(accessToken, order, businessName, businessLogo);
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>GET /api/pedido/{accessToken}/teaser — vista previa mínima pública.</summary>
    [HttpGet("/api/pedido/{accessToken}/teaser")]
    [EnableRateLimiting(SecurityRateLimitPolicies.PublicTokenRead)]
    public async Task<ActionResult<OrderTeaserDto>> Teaser(string accessToken)
    {
        var order = await LoadOrderAsync(accessToken);
        if (order == null) return NotFound(new { message = "Pedido no encontrado." });

        var biz = await _db.Businesses.AsNoTracking()
            .Where(b => b.Id == order.BusinessId)
            .Select(b => new { b.Name, b.LogoUrl, b.MessengerUrl, b.FacebookUrl })
            .FirstOrDefaultAsync();

        return Ok(new OrderTeaserDto(
            BusinessName: biz?.Name ?? "Tu tienda",
            BusinessLogoUrl: biz?.LogoUrl,
            BusinessMessengerUrl: biz?.MessengerUrl,
            BusinessFacebookUrl: biz?.FacebookUrl,
            ClientName: order.Client?.Name ?? "Cliente",
            Total: order.Total,
            ItemsCount: order.Items.Count,
            Status: order.Status.ToString(),
            StatusLabel: StatusLabel(order.Status),
            ScheduledDeliveryDate: order.ScheduledDeliveryDate,
            IsExpired: order.ExpiresAt < DateTime.UtcNow,
            // Solo se expone a quien posee el token del pedido, para pre-llenar
            // el registro passwordless (y disparar el match por teléfono).
            ClientPhone: order.Client?.Phone));
    }

    /// <summary>Android App Links: verifica el dominio para /o/ y /pedido/.</summary>
    [HttpGet("/.well-known/assetlinks.json")]
    public IActionResult AssetLinks()
    {
        // Huellas SHA-256 de los certificados que firman la app. Configurables
        // en Render (App__AndroidCertFingerprints, separadas por coma) para
        // añadir la de Play App Signing al publicar. Default: la del debug.
        var configured = _config["App:AndroidCertFingerprints"];
        var fingerprints = string.IsNullOrWhiteSpace(configured)
            ? new[] { DefaultDebugFingerprint }
            : configured
                .Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var payload = new[]
        {
            new
            {
                relation = new[] { "delegate_permission/common.handle_all_urls" },
                target = new
                {
                    @namespace = "android_app",
                    package_name = AndroidPackage,
                    sha256_cert_fingerprints = fingerprints,
                },
            },
        };
        return Content(JsonPretty(payload), "application/json");
    }

    /// <summary>iOS Universal Links: asocia el dominio con la app.</summary>
    [HttpGet("/.well-known/apple-app-site-association")]
    public IActionResult AppleAppSiteAssociation()
    {
        var appId = _config["App:AppleAppId"];
        if (string.IsNullOrWhiteSpace(appId)) appId = DefaultAppleAppId;

        var payload = new
        {
            applinks = new
            {
                apps = Array.Empty<string>(),
                details = new[]
                {
                    new { appID = appId, paths = new[] { "/o/*", "/pedido/*", "/store/*" } },
                },
            },
        };
        // Apple exige application/json (sin extensión en la ruta).
        return Content(JsonPretty(payload), "application/json");
    }

    private static string JsonPretty(object value) =>
        System.Text.Json.JsonSerializer.Serialize(
            value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    // ── helpers ──

    private Task<Order?> LoadOrderAsync(string token) => _db.Orders
        .AsNoTracking()
        .Include(o => o.Client)
        .Include(o => o.Items)
        .FirstOrDefaultAsync(o => o.AccessToken == token);

    /// <summary>
    /// Registra una impresión del muro /o/{token}. Se ejecuta server-side para
    /// que no dependa de JS (in-app webviews con scripts bloqueados). Si el
    /// token no corresponde a ningún pedido, lo deja en el tenant default.
    /// </summary>
    private async Task RecordImpressionAsync(string accessToken, int? businessId)
    {
        if (businessId is null) return;

        try
        {
            var ua = Request.Headers["User-Agent"].ToString();
            _db.LinkEvents.Add(new LinkEvent
            {
                BusinessId = businessId.Value,
                OrderAccessToken = accessToken,
                Event = "impression",
                UserAgent = string.IsNullOrWhiteSpace(ua) ? null : Truncate(ua, 512),
                IpAddress = ExtractIp(),
            });
            await _db.SaveChangesAsync();
        }
        catch
        {
            // La analítica nunca debe romper la landing: si falla la inserción
            // (p. ej. constraint raro), seguimos sirviendo el HTML.
        }
    }

    private string? ExtractIp()
    {
        var remote = HttpContext.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrWhiteSpace(remote) ? null : Truncate(remote, 64);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);

    private string BuildLandingHtml(
        string token, Order? order, string? businessName, string? businessLogo)
    {
        var hasOrder = order != null && order.ExpiresAt >= DateTime.UtcNow;
        var isExpired = order != null && order.ExpiresAt < DateTime.UtcNow;

        var store = HtmlEncode(businessName ?? "Tu tienda");
        var scheme = $"nenis://o/{Uri.EscapeDataString(token)}";

        // Botones de descarga (provisionales hasta publicar). Configurables por
        // ops vía App:AndroidStoreUrl / App:IosStoreUrl sin redeploy.
        var androidStore = _config["App:AndroidStoreUrl"];
        var iosStore = _config["App:IosStoreUrl"];
        var androidHref = BuildStoreHref(androidStore, token);

        // "Abrir en la app" robusto para navegadores in-app (Messenger, WhatsApp,
        // Instagram) donde App Links NO dispara: en Android usamos intent:// que
        // abre la app instalada y, si no está y hay tienda configurada, cae a ella
        // (browser_fallback_url). En iOS usamos el scheme nenis://.
        var intentFallback = androidHref != null
            ? $"S.browser_fallback_url={Uri.EscapeDataString(androidHref)};"
            : "";
        var intentUrl =
            $"intent://app.nenisapp.com/o/{Uri.EscapeDataString(token)}#Intent;scheme=https;package={AndroidPackage};{intentFallback}end";

        string teaser;
        if (hasOrder)
        {
            var clientName = HtmlEncode(order!.Client?.Name ?? "bonita");
            var total = order.Total.ToString("C0", EsMx);
            var status = HtmlEncode(StatusLabel(order.Status));
            var logo = string.IsNullOrWhiteSpace(businessLogo)
                ? ""
                : $"<img class=\"logo\" src=\"{HtmlEncode(businessLogo)}\" alt=\"{store}\"/>";
            teaser = $$"""
            <div class="card">
              {{logo}}
              <div class="eyebrow">{{store}}</div>
              <div class="hi">Hola {{clientName}} 🌸</div>
              <div class="row"><span>Tu pedido</span><b>{{total}}</b></div>
              <div class="status">{{status}}</div>
            </div>
            """;
        }
        else if (isExpired)
        {
            teaser = """
            <div class="card">
              <div class="hi">Este enlace expiró ⌛</div>
              <p class="muted">Descarga la app para ver tus pedidos, puntos y avisos en un solo lugar.</p>
            </div>
            """;
        }
        else
        {
            teaser = """
            <div class="card">
              <div class="hi">Tu pedido te espera 🌸</div>
              <p class="muted">Descarga la app para verlo, seguir tu entrega en vivo y juntar puntos.</p>
            </div>
            """;
        }

        var androidBtn = androidHref == null
            ? "<div class=\"pill soon\">Android · muy pronto</div>"
            : $"<a class=\"pill secondary\" href=\"{HtmlEncode(androidHref)}\" data-track=\"store_android\">Descargar para Android</a>";
        var iosBtn = string.IsNullOrWhiteSpace(iosStore)
            ? "<div class=\"pill soon\">iPhone · muy pronto</div>"
            : $"<a class=\"pill secondary\" href=\"{HtmlEncode(iosStore)}\" data-track=\"store_ios\">Descargar para iPhone</a>";

        // Smart App Banner iOS: si la app está publicada y tenemos el App Store
        // ID (config App:AppleStoreId), iOS muestra el banner nativo "Abrir en
        // la app" encima de la página. app-argument lleva el deep link para que
        // la app abra directo en el pedido.
        var appleStoreId = _config["App:AppleStoreId"];
        var smartAppBanner = string.IsNullOrWhiteSpace(appleStoreId)
            ? string.Empty
            : $"<meta name=\"apple-itunes-app\" content=\"app-id={HtmlEncode(appleStoreId)}, app-argument={HtmlEncode(scheme)}\"/>";

        return $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head>
          <meta charset="utf-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover"/>
          <meta name="robots" content="noindex"/>
          {{smartAppBanner}}
          <title>Neni's · Tu pedido</title>
          <style>
            :root { --pink:#FF0072; --pink2:#FF3D8B; --cream:#FFF4F7; --ink:#2A1622; --muted:#8A7580; }
            * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
            body { margin:0; min-height:100vh; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;
                   background:radial-gradient(120% 80% at 50% -10%, #FFE1EC 0%, var(--cream) 55%); color:var(--ink);
                   display:flex; align-items:center; justify-content:center; padding:24px; }
            .wrap { width:100%; max-width:400px; text-align:center; }
            .brand { font-weight:800; font-size:22px; letter-spacing:.3px; color:var(--pink); margin-bottom:18px; }
            .card { background:#fff; border-radius:24px; padding:22px; box-shadow:0 18px 44px -22px rgba(255,0,114,.45);
                    text-align:left; margin-bottom:20px; }
            .logo { width:52px; height:52px; border-radius:16px; object-fit:cover; margin-bottom:10px; }
            .eyebrow { font-size:12px; font-weight:700; text-transform:uppercase; letter-spacing:.6px; color:var(--pink); }
            .hi { font-size:20px; font-weight:800; margin:6px 0 12px; }
            .row { display:flex; justify-content:space-between; align-items:center; font-size:15px; color:var(--muted); }
            .row b { font-size:22px; color:var(--ink); }
            .status { margin-top:12px; display:inline-block; background:#FFE1EC; color:var(--pink);
                      font-weight:700; font-size:13px; padding:6px 12px; border-radius:99px; }
            .muted { color:var(--muted); font-size:14px; line-height:1.5; margin:8px 0 0; }
            .headline { font-size:18px; font-weight:800; margin:0 0 4px; }
            .sub { color:var(--muted); font-size:13.5px; margin:0 0 18px; }
            .pill { display:block; width:100%; text-decoration:none; text-align:center; font-weight:800; font-size:16px;
                    padding:16px; border-radius:99px; margin-bottom:12px;
                    background:linear-gradient(135deg,var(--pink2),var(--pink)); color:#fff;
                    box-shadow:0 12px 26px -12px rgba(255,0,114,.7); }
            .pill.soon { background:#F3E4EC; color:var(--muted); box-shadow:none; }
            .pill.secondary { background:#fff; color:var(--pink); border:1.5px solid #FFD0E2; box-shadow:none; }
            .hint { color:var(--muted); font-size:12px; margin:2px 0 14px; }
            .ghost { display:block; text-align:center; text-decoration:none; color:var(--pink); font-weight:700;
                     font-size:14px; padding:12px; }
          </style>
        </head>
        <body>
          <div class="wrap">
            <div class="brand">Neni's 🌸</div>
            {{teaser}}
            <p class="headline">Todo tu pedido, en la app</p>
            <p class="sub">Rastreo en vivo, pagos, puntos y avisos — de {{store}} y de todas tus tiendas.</p>
            <a class="pill" id="open-app" href="{{HtmlEncode(scheme)}}" data-android="{{HtmlEncode(intentUrl)}}" data-track="open_app">Abrir en la app</a>
            <p class="hint">¿Aún no la tienes? Descárgala:</p>
            {{androidBtn}}
            {{iosBtn}}
          </div>
          <script>
            (function () {
              var el = document.getElementById('open-app');
              if (!el) return;
              var android = el.getAttribute('data-android');
              // En Android preferimos intent:// (abre la app aunque el link venga
              // de un navegador in-app; si no está, cae a la tienda).
              if (android && /android/i.test(navigator.userAgent)) {
                el.setAttribute('href', android);
              }
            })();

            // Click tracking de analítica auto-alojada. Fire-and-forget: si
            // falla la red o el navegador bloquea el fetch, no rompemos la
            // navegación. La impresión ya quedó registrada server-side.
            (function () {
              var token = "{{HtmlEncode(token)}}";
              function track(event) {
                try {
                  var body = JSON.stringify({
                    AccessToken: token,
                    Event: event,
                    UserAgent: navigator.userAgent
                  });
                  fetch('/api/link-events', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: body,
                    keepalive: true
                  });
                } catch (_) { /* no-op */ }
              }
              document.querySelectorAll('[data-track]').forEach(function (a) {
                a.addEventListener('click', function () {
                  track(a.getAttribute('data-track'));
                });
              });
            })();
          </script>
        </body>
        </html>
        """;
    }

    /// <summary>
    /// Arma la URL de descarga de Android adjuntando el Install Referrer
    /// (`token=...`) para el deep link diferido. Devuelve null si no hay tienda
    /// configurada todavía (apps sin publicar).
    /// </summary>
    private static string? BuildStoreHref(string? androidStore, string token)
    {
        if (string.IsNullOrWhiteSpace(androidStore)) return null;
        var referrer = Uri.EscapeDataString($"token={token}");
        var sep = androidStore.Contains('?') ? "&" : "?";
        return $"{androidStore}{sep}referrer={referrer}";
    }

    private static string HtmlEncode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string StatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.Pending => "Pendiente por confirmar",
        OrderStatus.Confirmed => "Confirmado 💖",
        OrderStatus.Shipped => "Empacado 📦",
        OrderStatus.InRoute => "En ruta ✨",
        OrderStatus.Delivered => "Entregado 🌸",
        OrderStatus.NotDelivered => "No se pudo entregar",
        OrderStatus.Canceled => "Cancelado",
        OrderStatus.Postponed => "Pospuesto",
        _ => status.ToString(),
    };
}

/// <summary>Vista previa mínima pública de un pedido (para el muro / previews).</summary>
public record OrderTeaserDto(
    string BusinessName,
    string? BusinessLogoUrl,
    string? BusinessMessengerUrl,
    string? BusinessFacebookUrl,
    string ClientName,
    decimal Total,
    int ItemsCount,
    string Status,
    string StatusLabel,
    DateTime? ScheduledDeliveryDate,
    bool IsExpired,
    string? ClientPhone = null);
