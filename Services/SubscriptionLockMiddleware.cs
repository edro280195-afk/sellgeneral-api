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
        if (ShouldSkip(context, endpoint, currentTenant))
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
