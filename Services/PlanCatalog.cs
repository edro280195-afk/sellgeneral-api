using EntregasApi.Models;

namespace EntregasApi.Services;

public static class PlanTiers
{
    public const string Entrada = "Entrada";
    public const string Pro = "Pro";
    public const string Elite = "Elite";
    public const string Locked = "Bloqueado";
}

/// <summary>
/// Periodicidad del cobro de la suscripcion. Se traduce al campo
/// frequency/frequency_type de la API de preapproval de MP.
/// </summary>
public enum SubscriptionPeriodicity
{
    Monthly = 1,
    Quarterly = 3,
    Annual = 12
}

public static class SubscriptionPeriodicities
{
    public const string Monthly = "monthly";
    public const string Quarterly = "quarterly";
    public const string Annual = "annual";

    public static bool TryParse(string? value, out SubscriptionPeriodicity periodicity)
    {
        periodicity = SubscriptionPeriodicity.Monthly;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "mensual":
            case "monthly":
            case "1":
                periodicity = SubscriptionPeriodicity.Monthly;
                return true;
            case "trimestral":
            case "quarterly":
            case "3":
                periodicity = SubscriptionPeriodicity.Quarterly;
                return true;
            case "anual":
            case "annual":
            case "12":
                periodicity = SubscriptionPeriodicity.Annual;
                return true;
            default:
                return false;
        }
    }
}

public sealed record PlanDefinition(
    string Tier,
    IReadOnlySet<Feature> Features,
    IReadOnlyDictionary<LimitKey, int> Limits);

/// <summary>
/// Catalogo de planes mantenido en codigo para la fase 1.0. Si el negocio necesita
/// precios/features dinamicas, este catalogo puede migrar a tabla despues.
/// </summary>
public static class PlanCatalog
{
    private static readonly Feature[] BaseFeatures =
    [
        Feature.ManualOrders,
        Feature.ClientDirectory,
        Feature.PublicTrackingLink,
        Feature.OrderStatusPush,
        Feature.ClientAccount,
        Feature.Loyalty
    ];

    private static readonly Feature[] ProFeatures =
    [
        .. BaseFeatures,
        Feature.LivePush,
        Feature.LiveGpsTracking,
        Feature.Financials,
        Feature.TandasRaffles,
        Feature.Pos,
        Feature.FacebookImport,
        Feature.VipDrops
    ];

    private static readonly Feature[] EliteFeatures =
    [
        .. ProFeatures,
        Feature.CamiAssistant,
        Feature.TrafficRouteOptimization,
        Feature.Exports,
        Feature.PrioritySupport
    ];

    private static readonly PlanDefinition Locked = new(
        PlanTiers.Locked,
        new HashSet<Feature>(),
        new Dictionary<LimitKey, int>
        {
            [LimitKey.MaxDrivers] = 0,
            [LimitKey.RouteOptimizationCalls] = 0
        });

    private static readonly PlanDefinition Entrada = new(
        PlanTiers.Entrada,
        BaseFeatures.ToHashSet(),
        new Dictionary<LimitKey, int>
        {
            [LimitKey.MaxDrivers] = 1,
            [LimitKey.RouteOptimizationCalls] = 0
        });

    private static readonly PlanDefinition Pro = new(
        PlanTiers.Pro,
        ProFeatures.ToHashSet(),
        new Dictionary<LimitKey, int>
        {
            [LimitKey.MaxDrivers] = int.MaxValue,
            [LimitKey.RouteOptimizationCalls] = 0
        });

    private static readonly PlanDefinition Elite = new(
        PlanTiers.Elite,
        EliteFeatures.ToHashSet(),
        new Dictionary<LimitKey, int>
        {
            [LimitKey.MaxDrivers] = int.MaxValue,
            [LimitKey.RouteOptimizationCalls] = int.MaxValue
        });

    public static PlanDefinition Get(string? planTier)
    {
        return Normalize(planTier) switch
        {
            PlanTiers.Pro => Pro,
            PlanTiers.Elite => Elite,
            PlanTiers.Locked => Locked,
            _ => Entrada
        };
    }

    public static string Normalize(string? planTier)
    {
        var value = planTier?.Trim();

        if (string.Equals(value, PlanTiers.Pro, StringComparison.OrdinalIgnoreCase))
        {
            return PlanTiers.Pro;
        }

        if (string.Equals(value, PlanTiers.Elite, StringComparison.OrdinalIgnoreCase))
        {
            return PlanTiers.Elite;
        }

        if (string.Equals(value, PlanTiers.Locked, StringComparison.OrdinalIgnoreCase))
        {
            return PlanTiers.Locked;
        }

        return PlanTiers.Entrada;
    }

    public static bool TryNormalizeSelectablePlan(string? planTier, out string normalizedPlanTier)
    {
        var value = planTier?.Trim();
        normalizedPlanTier = Normalize(value);
        return string.Equals(value, PlanTiers.Entrada, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, PlanTiers.Pro, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, PlanTiers.Elite, StringComparison.OrdinalIgnoreCase);
    }

    public static int GetRank(string? planTier)
    {
        return Normalize(planTier) switch
        {
            PlanTiers.Elite => 3,
            PlanTiers.Pro => 2,
            PlanTiers.Entrada => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Precio MENSUAL base (MXN) de un plan. Se usa como insumo para calcular
    /// el monto a cobrar segun la periodicidad elegida por el owner.
    /// </summary>
    public static decimal GetMonthlyPrice(string? planTier)
    {
        return Normalize(planTier) switch
        {
            PlanTiers.Entrada => 129m,
            PlanTiers.Pro => 250m,
            PlanTiers.Elite => 460m,
            _ => 0m
        };
    }

    /// <summary>
    /// Calcula el monto TOTAL a cobrar en cada ciclo segun la periodicidad.
    /// Para Quarterly aplica 10% de descuento sobre 3 meses; para Annual 20%
    /// sobre 12 meses. Si <paramref name="quarterlyDiscountPct"/> o
    /// <paramref name="annualDiscountPct"/> vienen en null/0, no hay descuento.
    /// </summary>
    public static decimal GetPeriodAmount(
        string? planTier,
        SubscriptionPeriodicity periodicity,
        decimal quarterlyDiscountPct = 10m,
        decimal annualDiscountPct = 20m)
    {
        var monthly = GetMonthlyPrice(planTier);
        return periodicity switch
        {
            SubscriptionPeriodicity.Monthly => monthly,
            SubscriptionPeriodicity.Quarterly => ApplyDiscount(
                monthly * (int)SubscriptionPeriodicity.Quarterly,
                quarterlyDiscountPct),
            SubscriptionPeriodicity.Annual => ApplyDiscount(
                monthly * (int)SubscriptionPeriodicity.Annual,
                annualDiscountPct),
            _ => monthly
        };
    }

    private static decimal ApplyDiscount(decimal amount, decimal discountPct)
    {
        if (discountPct <= 0m)
        {
            return amount;
        }

        var factor = 1m - (discountPct / 100m);
        return Math.Round(amount * factor, 2, MidpointRounding.AwayFromZero);
    }

    public static string GetRequiredPlan(Feature feature)
    {
        return feature switch
        {
            Feature.CamiAssistant or
            Feature.TrafficRouteOptimization or
            Feature.Exports or
            Feature.PrioritySupport => PlanTiers.Elite,

            Feature.LivePush or
            Feature.LiveGpsTracking or
            Feature.Financials or
            Feature.TandasRaffles or
            Feature.Pos or
            Feature.FacebookImport or
            Feature.VipDrops => PlanTiers.Pro,

            _ => PlanTiers.Entrada
        };
    }

    public static string GetRequiredPlan(LimitKey limitKey)
    {
        return limitKey switch
        {
            LimitKey.RouteOptimizationCalls => PlanTiers.Elite,
            LimitKey.MaxDrivers => PlanTiers.Pro,
            _ => PlanTiers.Pro
        };
    }
}
