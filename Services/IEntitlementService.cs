using EntregasApi.Models;

namespace EntregasApi.Services;

public sealed record SubscriptionSnapshot(
    string EffectivePlanTier,
    string PlanTier,
    SubscriptionStatus SubscriptionStatus,
    DateTime? TrialEndsAt,
    DateTime? CurrentPeriodEndsAt,
    string? PendingPlanTier,
    DateTime? PendingPlanEffectiveAt,
    bool IsLocked,
    int DaysLeft,
    int PastDueGraceDays);

public interface IEntitlementService
{
    Task<bool> HasFeatureAsync(Feature feature, CancellationToken cancellationToken = default);
    Task<int> GetLimitAsync(LimitKey limitKey, CancellationToken cancellationToken = default);
    Task<string> EffectivePlanTierAsync(CancellationToken cancellationToken = default);
    Task<SubscriptionSnapshot> GetSubscriptionSnapshotAsync(CancellationToken cancellationToken = default);
    Task EnsureWithinLimitAsync(LimitKey limitKey, int currentCount, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Feature>> GetEnabledFeaturesAsync(CancellationToken cancellationToken = default);
}
