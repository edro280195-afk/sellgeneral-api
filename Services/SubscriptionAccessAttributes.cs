namespace EntregasApi.Services;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipTenantResolutionAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class BypassSubscriptionLockAttribute : Attribute
{
}
