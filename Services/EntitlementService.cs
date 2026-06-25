using EntregasApi.Models;
using EntregasApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EntregasApi.Services;

public class EntitlementService : IEntitlementService
{
    private readonly AppDbContext _db;
    private readonly ICurrentBusiness _currentBusiness;
    private readonly TimeProvider _timeProvider;
    private readonly int _pastDueGraceDays;
    private SubscriptionSnapshot? _cachedSnapshot;
    private PlanDefinition? _cachedPlan;

    public EntitlementService(
        AppDbContext db,
        ICurrentBusiness currentBusiness,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentBusiness = currentBusiness;
        _timeProvider = timeProvider;
        _pastDueGraceDays = Math.Max(0, configuration.GetValue<int?>("Subscriptions:PastDueGraceDays") ?? 3);
    }

    public async Task<bool> HasFeatureAsync(
        Feature feature,
        CancellationToken cancellationToken = default)
    {
        var plan = await GetEffectivePlanAsync(cancellationToken);
        return plan.Features.Contains(feature);
    }

    public async Task<int> GetLimitAsync(
        LimitKey limitKey,
        CancellationToken cancellationToken = default)
    {
        var plan = await GetEffectivePlanAsync(cancellationToken);
        return plan.Limits.TryGetValue(limitKey, out var limit) ? limit : 0;
    }

    public async Task<string> EffectivePlanTierAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSubscriptionSnapshotAsync(cancellationToken);
        return snapshot.EffectivePlanTier;
    }

    public async Task<SubscriptionSnapshot> GetSubscriptionSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedSnapshot is not null)
        {
            return _cachedSnapshot;
        }

        var business = await _currentBusiness.GetAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var status = await RecalculateStatusIfNeededAsync(business, now, cancellationToken);
        var effectivePlanTier = ResolveEffectivePlanTier(business, status, now);
        var daysLeft = CalculateDaysLeft(status, business.TrialEndsAt, business.CurrentPeriodEndsAt, now);

        _cachedSnapshot = new SubscriptionSnapshot(
            effectivePlanTier,
            PlanCatalog.Normalize(business.PlanTier),
            status,
            business.TrialEndsAt,
            business.CurrentPeriodEndsAt,
            string.IsNullOrWhiteSpace(business.PendingPlanTier)
                ? null
                : PlanCatalog.Normalize(business.PendingPlanTier),
            business.PendingPlanEffectiveAt,
            string.Equals(effectivePlanTier, PlanTiers.Locked, StringComparison.Ordinal),
            daysLeft,
            _pastDueGraceDays);

        return _cachedSnapshot;
    }

    public async Task EnsureWithinLimitAsync(
        LimitKey limitKey,
        int currentCount,
        CancellationToken cancellationToken = default)
    {
        var limit = await GetLimitAsync(limitKey, cancellationToken);
        if (currentCount >= limit)
        {
            throw new EntitlementLimitExceededException(
                limitKey,
                PlanCatalog.GetRequiredPlan(limitKey),
                limit);
        }
    }

    public async Task<IReadOnlyList<Feature>> GetEnabledFeaturesAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = await GetEffectivePlanAsync(cancellationToken);
        return plan.Features
            .OrderBy(f => f.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private async Task<PlanDefinition> GetEffectivePlanAsync(CancellationToken cancellationToken)
    {
        if (_cachedPlan is not null)
        {
            return _cachedPlan;
        }

        var snapshot = await GetSubscriptionSnapshotAsync(cancellationToken);
        _cachedPlan = PlanCatalog.Get(snapshot.EffectivePlanTier);

        return _cachedPlan;
    }

    private async Task<SubscriptionStatus> RecalculateStatusIfNeededAsync(
        Business business,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var shouldExpire =
            business.SubscriptionStatus == SubscriptionStatus.Trialing &&
            business.TrialEndsAt is not null &&
            business.TrialEndsAt <= now;

        shouldExpire |=
            business.SubscriptionStatus == SubscriptionStatus.PastDue &&
            !IsWithinPastDueGrace(business.CurrentPeriodEndsAt, now);

        if (!shouldExpire)
        {
            return business.SubscriptionStatus;
        }

        var tracked = await _db.Businesses
            .FirstOrDefaultAsync(b => b.Id == business.Id, cancellationToken);

        if (tracked is not null && tracked.SubscriptionStatus != SubscriptionStatus.Expired)
        {
            tracked.SubscriptionStatus = SubscriptionStatus.Expired;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return SubscriptionStatus.Expired;
    }

    private string ResolveEffectivePlanTier(
        Business business,
        SubscriptionStatus status,
        DateTime now)
    {
        return status switch
        {
            SubscriptionStatus.Trialing when business.TrialEndsAt is not null && business.TrialEndsAt > now
                => PlanTiers.Pro,

            SubscriptionStatus.Active
                => PlanCatalog.Normalize(business.PlanTier),

            SubscriptionStatus.PastDue when IsWithinPastDueGrace(business.CurrentPeriodEndsAt, now)
                => PlanCatalog.Normalize(business.PlanTier),

            _ => PlanTiers.Locked
        };
    }

    private int CalculateDaysLeft(
        SubscriptionStatus status,
        DateTime? trialEndsAt,
        DateTime? currentPeriodEndsAt,
        DateTime now)
    {
        var targetDate = status switch
        {
            SubscriptionStatus.Trialing => trialEndsAt,
            SubscriptionStatus.PastDue => currentPeriodEndsAt?.AddDays(_pastDueGraceDays),
            SubscriptionStatus.Active => currentPeriodEndsAt,
            _ => null
        };

        if (targetDate is null || targetDate <= now)
        {
            return 0;
        }

        return Math.Max(0, (int)Math.Ceiling((targetDate.Value - now).TotalDays));
    }

    private bool IsWithinPastDueGrace(DateTime? currentPeriodEndsAt, DateTime now)
    {
        return currentPeriodEndsAt is not null
               && currentPeriodEndsAt.Value.AddDays(_pastDueGraceDays) > now;
    }
}

public sealed class EntitlementLimitExceededException : InvalidOperationException
{
    public EntitlementLimitExceededException(LimitKey limitKey, string requiredPlan, int limit)
        : base($"El limite {limitKey} requiere plan {requiredPlan}.")
    {
        LimitKey = limitKey;
        RequiredPlan = requiredPlan;
        Limit = limit;
    }

    public LimitKey LimitKey { get; }
    public string RequiredPlan { get; }
    public int Limit { get; }
}
