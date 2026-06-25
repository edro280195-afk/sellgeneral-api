using System.Security.Claims;
using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public class TenantResolutionMiddleware
{
    private const string BusinessHeader = "X-Business-Id";
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        AppDbContext db,
        ICurrentTenant currentTenant,
        ILogger<TenantResolutionMiddleware> logger)
    {
        var endpoint = context.GetEndpoint();
        var allowsAnonymous = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var requiresAuthorization = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
        var skipsTenantResolution = endpoint?.Metadata.GetMetadata<SkipTenantResolutionAttribute>() is not null;

        if (skipsTenantResolution)
        {
            await _next(context);
            return;
        }

        if (!requiresAuthorization || allowsAnonymous)
        {
            await ResolvePublicTokenTenantAsync(context, db, currentTenant);
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var resolved = await ResolveAuthenticatedTenantAsync(context, db, currentTenant, logger);
            if (!resolved)
            {
                return;
            }
        }

        await _next(context);
    }

    private static async Task<bool> ResolveAuthenticatedTenantAsync(
        HttpContext context,
        AppDbContext db,
        ICurrentTenant currentTenant,
        ILogger logger)
    {
        var accountId = ReadAccountId(context.User);
        if (accountId is null)
        {
            return true;
        }

        var memberships = await db.Memberships
            .AsNoTracking()
            .Include(m => m.Business)
            .Where(m => m.AccountId == accountId.Value)
            .ToListAsync(context.RequestAborted);

        if (memberships.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "La cuenta no tiene acceso a ningun negocio." });
            return false;
        }

        var requestedBusinessId = context.Request.Headers.TryGetValue(BusinessHeader, out var headerValue)
            ? headerValue.ToString()
            : null;

        Membership? membership;
        if (string.IsNullOrWhiteSpace(requestedBusinessId))
        {
            if (memberships.Count > 1)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { message = "X-Business-Id es obligatorio para cuentas con mas de un negocio." });
                return false;
            }

            membership = memberships[0];
        }
        else if (!int.TryParse(requestedBusinessId, out var businessId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { message = "X-Business-Id invalido." });
            return false;
        }
        else
        {
            membership = memberships.FirstOrDefault(m => m.BusinessId == businessId);
        }

        if (membership is null || membership.Business?.IsActive == false)
        {
            logger.LogWarning(
                "Account {AccountId} intento acceder al Business {BusinessId} sin membership valida.",
                accountId,
                requestedBusinessId);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "No tienes acceso a ese negocio." });
            return false;
        }

        currentTenant.SetBusiness(membership.BusinessId);
        return true;
    }

    private static async Task ResolvePublicTokenTenantAsync(
        HttpContext context,
        AppDbContext db,
        ICurrentTenant currentTenant)
    {
        if (TryRouteValue(context, "accessToken", out var accessToken))
        {
            var businessId = await db.Orders
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(o => o.AccessToken == accessToken)
                .Select(o => (int?)o.BusinessId)
                .FirstOrDefaultAsync(context.RequestAborted);

            if (businessId.HasValue)
            {
                currentTenant.SetBusiness(businessId.Value);
            }

            return;
        }

        if (TryRouteValue(context, "driverToken", out var driverToken))
        {
            var businessId = await db.DeliveryRoutes
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => r.DriverToken == driverToken)
                .Select(r => (int?)r.BusinessId)
                .FirstOrDefaultAsync(context.RequestAborted);

            if (businessId.HasValue)
            {
                currentTenant.SetBusiness(businessId.Value);
            }

            return;
        }

        if (TryRouteValue(context, "token", out var token) &&
            context.Request.Path.StartsWithSegments("/api/public-tanda"))
        {
            var businessId = await db.Tandas
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.AccessToken == token)
                .Select(t => (int?)t.BusinessId)
                .FirstOrDefaultAsync(context.RequestAborted);

            if (businessId.HasValue)
            {
                currentTenant.SetBusiness(businessId.Value);
            }

            return;
        }

        if (TryRouteValue(context, "routeId", out var routeIdRaw) &&
            int.TryParse(routeIdRaw, out var routeId) &&
            context.Request.Path.StartsWithSegments("/api/Cami/route-briefing"))
        {
            var businessId = await db.DeliveryRoutes
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => r.Id == routeId)
                .Select(r => (int?)r.BusinessId)
                .FirstOrDefaultAsync(context.RequestAborted);

            if (businessId.HasValue)
            {
                currentTenant.SetBusiness(businessId.Value);
            }
        }
    }

    private static bool TryRouteValue(HttpContext context, string key, out string value)
    {
        value = context.Request.RouteValues.TryGetValue(key, out var raw)
            ? Convert.ToString(raw) ?? ""
            : "";

        return !string.IsNullOrWhiteSpace(value);
    }

    private static int? ReadAccountId(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("account_id")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");

        return int.TryParse(raw, out var accountId) ? accountId : null;
    }
}
