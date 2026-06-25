using EntregasApi.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace EntregasApi.Services;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresFeatureAttribute : Attribute, IAsyncActionFilter
{
    public RequiresFeatureAttribute(Feature feature)
    {
        Feature = feature;
    }

    public Feature Feature { get; }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var entitlements = context.HttpContext.RequestServices.GetRequiredService<IEntitlementService>();
        var hasFeature = await entitlements.HasFeatureAsync(Feature, context.HttpContext.RequestAborted);

        if (!hasFeature)
        {
            context.Result = new ObjectResult(new
            {
                error = "feature_locked",
                feature = Feature.ToString(),
                requiredPlan = PlanCatalog.GetRequiredPlan(Feature)
            })
            {
                StatusCode = StatusCodes.Status402PaymentRequired
            };
            return;
        }

        await next();
    }
}
