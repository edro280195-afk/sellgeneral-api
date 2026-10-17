using Microsoft.AspNetCore.Authorization;

namespace EntregasApi.Services;

public sealed class SubscriptionLockMiddleware
{
    private readonly RequestDelegate _next;

    public SubscriptionLockMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentTenant currentTenant,
        IEntitlementService entitlements)
    {
        var endpoint = context.GetEndpoint();
        var isPublicShareLink = IsPublicShareLinkRequest(context);
        if (HttpMethods.IsOptions(context.Request.Method) ||
            (!isPublicShareLink && ShouldSkip(context, endpoint, currentTenant)) ||
            (isPublicShareLink && !currentTenant.IsResolved))
        {
            await _next(context);
            return;
        }

        var snapshot = await entitlements.GetSubscriptionSnapshotAsync(context.RequestAborted);
        if (!snapshot.IsLocked)
        {
            await _next(context);
            return;
        }

        if (isPublicShareLink)
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
            if (context.Request.GetTypedHeaders().Accept?.Any(
                    mediaType => string.Equals(
                        mediaType.MediaType.Value,
                        "text/html",
                        StringComparison.OrdinalIgnoreCase)) == true)
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(BuildUnavailableLinkHtml(), context.RequestAborted);
                return;
            }

            await context.Response.WriteAsJsonAsync(new
            {
                error = "subscription_locked",
                message = "Este enlace ya no está disponible. Pídele a la vendedora que reactive su tienda."
            }, context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "subscription_locked",
            effectivePlan = snapshot.EffectivePlanTier,
            planTier = snapshot.PlanTier,
            subscriptionStatus = snapshot.SubscriptionStatus.ToString(),
            snapshot.TrialEndsAt,
            snapshot.CurrentPeriodEndsAt,
            snapshot.PendingPlanTier,
            snapshot.PendingPlanEffectiveAt,
            snapshot.IsLocked,
            snapshot.DaysLeft
        }, context.RequestAborted);
    }

    private static bool IsPublicShareLinkRequest(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/api/pedido") ||
               path.StartsWithSegments("/pedido") ||
               path.StartsWithSegments("/o") ||
               path.StartsWithSegments("/tanda-view") ||
               path.StartsWithSegments("/api/public-tanda");
    }

    private static string BuildUnavailableLinkHtml() => """
        <!doctype html>
        <html lang="es">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <meta name="robots" content="noindex" />
          <title>Neni's · Enlace pausado</title>
          <style>
            * { box-sizing: border-box; }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px;
              background: linear-gradient(145deg, #fff4f7, #f8f2ff); color: #3f2432;
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; text-align: center; }
            main { width: min(100%, 420px); padding: 34px 28px; background: white; border: 1px solid #fce1ed;
              border-radius: 28px; box-shadow: 0 20px 54px -28px #be185d88; }
            .icon { font-size: 48px; }
            h1 { margin: 14px 0 8px; color: #9d174d; font-size: 24px; }
            p { margin: 0; color: #8a7580; line-height: 1.55; }
            .brand { margin-top: 24px; color: #ec4899; font-weight: 800; letter-spacing: .2px; }
          </style>
        </head>
        <body>
          <main>
            <div class="icon">🌸</div>
            <h1>Este enlace está pausado</h1>
            <p>Esta tienda no tiene un plan activo. Pídele a la vendedora que reactive su suscripción para volver a compartir sus enlaces.</p>
            <div class="brand">Neni's App</div>
          </main>
        </body>
        </html>
        """;

    private static bool ShouldSkip(
        HttpContext context,
        Endpoint? endpoint,
        ICurrentTenant currentTenant)
    {
        if (endpoint is null)
        {
            return true;
        }

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null ||
            endpoint.Metadata.GetMetadata<BypassSubscriptionLockAttribute>() is not null)
        {
            return true;
        }

        var requiresAuthorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
        if (!requiresAuthorization)
        {
            return true;
        }

        if (context.User.Identity?.IsAuthenticated != true || !currentTenant.IsResolved)
        {
            return true;
        }

        return false;
    }
}
