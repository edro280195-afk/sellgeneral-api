using System.Globalization;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/business/subscription")]
public class SubscriptionController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentBusiness _currentBusiness;
    private readonly IEntitlementService _entitlements;
    private readonly IMercadoPagoSubscriptionService _mp;
    private readonly TimeProvider _timeProvider;
    private readonly MercadoPagoSubscriptionOptions _mpOptions;
    private readonly ILogger<SubscriptionController> _logger;

    public SubscriptionController(
        AppDbContext db,
        ICurrentTenant currentTenant,
        ICurrentBusiness currentBusiness,
        IEntitlementService entitlements,
        IMercadoPagoSubscriptionService mp,
        TimeProvider timeProvider,
        IOptions<MercadoPagoSubscriptionOptions> mpOptions,
        ILogger<SubscriptionController> logger)
    {
        _db = db;
        _currentTenant = currentTenant;
        _currentBusiness = currentBusiness;
        _entitlements = entitlements;
        _mp = mp;
        _timeProvider = timeProvider;
        _mpOptions = mpOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Llave publica de MP de la PLATAFORMA. La consume el brick de FE-4
    /// para tokenizar la tarjeta del owner. NUNCA es la del tenant.
    /// </summary>
    [HttpGet("preapproval/public-key")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public ActionResult<PlatformMpPublicKeyDto> GetPlatformPublicKey()
    {
        return Ok(new PlatformMpPublicKeyDto(_mpOptions.PublicKey));
    }

    /// <summary>
    /// Catalogo publico (para el owner) de precios por plan y periodicidad.
    /// </summary>
    [HttpGet("pricing")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public ActionResult<SubscriptionPricingDto> GetPricing()
    {
        var quarterlyPct = (int)Math.Round(QuarterlyDiscountPct());
        var annualPct = (int)Math.Round(AnnualDiscountPct());
        var plans = new[]
        {
            PlanTiers.Entrada,
            PlanTiers.Pro,
            PlanTiers.Elite
        }
        .Select(tier => new SubscriptionPlanPriceDto(
            tier,
            PlanCatalog.GetPeriodAmount(tier, SubscriptionPeriodicity.Monthly,
                quarterlyDiscountPct: 0m, annualDiscountPct: 0m),
            PlanCatalog.GetPeriodAmount(tier, SubscriptionPeriodicity.Quarterly,
                quarterlyDiscountPct: quarterlyPct, annualDiscountPct: 0m),
            PlanCatalog.GetPeriodAmount(tier, SubscriptionPeriodicity.Annual,
                quarterlyDiscountPct: 0m, annualDiscountPct: annualPct),
            quarterlyPct,
            annualPct,
            _mpOptions.Currency))
        .ToList();

        return Ok(new SubscriptionPricingDto(plans, _mpOptions.Currency));
    }

    /// <summary>
    /// Crea el preapproval de MP y deja la cuenta en estado Active.
    /// Idempotente: si ya hay un preapproval activo, lo actualiza.
    /// </summary>
    [HttpPost("preapproval")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<ActionResult<PreapprovalSummaryDto>> CreatePreapproval(
        [FromBody] CreatePreapprovalRequest request,
        CancellationToken cancellationToken)
    {
        if (!PlanCatalog.TryNormalizeSelectablePlan(request.PlanTier, out var planTier))
        {
            return BadRequest(new { message = "Plan invalido. Usa Entrada, Pro o Elite." });
        }

        if (!SubscriptionPeriodicities.TryParse(request.Periodicity, out var periodicity))
        {
            return BadRequest(new { message = "Periodicidad invalida. Usa monthly, quarterly o annual." });
        }

        var email = request.PayerEmail?.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return BadRequest(new { message = "PayerEmail es obligatorio y debe ser un correo valido." });
        }

        var business = await _db.Businesses
            .FirstOrDefaultAsync(b => b.Id == _currentTenant.ActiveBusinessId, cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var externalReference = $"business_{business.Id}";

        // Si ya hay un preapproval autorizado en MP, lo tratamos como upgrade
        // (cambia plan/periodicidad y mantiene activa la suscripcion).
        if (!string.IsNullOrWhiteSpace(business.PreapprovalId) &&
            !string.Equals(business.PreapprovalStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return await UpdateExistingPreapprovalAsync(
                business, planTier, periodicity, cancellationToken);
        }

        MercadoPagoPreapproval preapproval;
        try
        {
            preapproval = await _mp.CreatePreapprovalAsync(
                business,
                planTier,
                periodicity,
                email,
                string.IsNullOrWhiteSpace(request.CardTokenId) ? null : request.CardTokenId,
                externalReference,
                firstChargeDate: business.TrialEndsAt ?? now,
                cancellationToken);
        }
        catch (MercadoPagoSubscriptionException ex)
        {
            _logger.LogWarning(ex, "[Subscription] MP rechazo la creacion del preapproval para {BusinessId}", business.Id);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = ex.Message
            });
        }

        ApplyPreapprovalToBusiness(business, planTier, periodicity, preapproval, now, isNew: true);
        await _db.SaveChangesAsync(cancellationToken);

        return ToSummary(business, preapproval);
    }

    /// <summary>
    /// Ajusta el plan y la periodicidad de la suscripcion activa. Si es
    /// upgrade, MP cobra el nuevo monto desde el siguiente ciclo; si es
    /// downgrade, se programa al fin del periodo actual.
    /// </summary>
    [HttpPut("preapproval")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<ActionResult<PreapprovalSummaryDto>> UpdatePreapproval(
        [FromBody] UpdatePreapprovalRequest request,
        CancellationToken cancellationToken)
    {
        if (!PlanCatalog.TryNormalizeSelectablePlan(request.PlanTier, out var planTier))
        {
            return BadRequest(new { message = "Plan invalido. Usa Entrada, Pro o Elite." });
        }

        if (!SubscriptionPeriodicities.TryParse(request.Periodicity, out var periodicity))
        {
            return BadRequest(new { message = "Periodicidad invalida." });
        }

        var business = await _db.Businesses
            .FirstOrDefaultAsync(b => b.Id == _currentTenant.ActiveBusinessId, cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        return await UpdateExistingPreapprovalAsync(business, planTier, periodicity, cancellationToken);
    }

    /// <summary>
    /// Cancela la suscripcion. La cuenta sigue activa hasta
    /// CurrentPeriodEndsAt; despues pasa a Expired y se bloquea (igual que
    /// en 1.2 cuando vence el trial).
    /// </summary>
    [HttpDelete("preapproval")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<ActionResult<PreapprovalSummaryDto>> CancelPreapproval(
        CancellationToken cancellationToken)
    {
        var business = await _db.Businesses
            .FirstOrDefaultAsync(b => b.Id == _currentTenant.ActiveBusinessId, cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        if (string.IsNullOrWhiteSpace(business.PreapprovalId))
        {
            return BadRequest(new { message = "No hay una suscripcion activa para cancelar." });
        }

        MercadoPagoPreapproval preapproval;
        try
        {
            preapproval = await _mp.CancelPreapprovalAsync(business.PreapprovalId, cancellationToken);
        }
        catch (MercadoPagoSubscriptionException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = ex.Message
            });
        }

        business.PreapprovalStatus = preapproval.Status;
        business.CancellationEffectiveAt = business.CurrentPeriodEndsAt;
        business.SubscriptionStatus = SubscriptionStatus.Canceled;
        await _db.SaveChangesAsync(cancellationToken);

        return ToSummary(business, preapproval);
    }

    private async Task<ActionResult<PreapprovalSummaryDto>> UpdateExistingPreapprovalAsync(
        Business business,
        string planTier,
        SubscriptionPeriodicity periodicity,
        CancellationToken cancellationToken)
    {
        MercadoPagoPreapproval preapproval;
        try
        {
            preapproval = await _mp.UpdatePreapprovalAsync(
                business.PreapprovalId!,
                planTier,
                periodicity,
                cancellationToken);
        }
        catch (MercadoPagoSubscriptionException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = ex.Message
            });
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var oldRank = PlanCatalog.GetRank(business.PlanTier);
        var newRank = PlanCatalog.GetRank(planTier);
        var isUpgrade = newRank > oldRank;

        if (isUpgrade)
        {
            business.PlanTier = planTier;
            business.PendingPlanTier = null;
            business.PendingPlanEffectiveAt = null;
            // Si esta bloqueada, reactivamos al upgrade inmediato.
            if (business.SubscriptionStatus is SubscriptionStatus.Expired or SubscriptionStatus.Canceled)
            {
                business.SubscriptionStatus = SubscriptionStatus.Active;
                business.TrialEndsAt = null;
            }
        }
        else
        {
            // Downgrade o mismo plan: la periodicidad se aplica ya
            // (afecta el monto cobrado en el siguiente ciclo) pero el plan
            // programado al fin del periodo.
            business.PendingPlanTier = planTier;
            business.PendingPlanEffectiveAt = business.CurrentPeriodEndsAt ?? now;
        }

        ApplyPeriodicity(business, periodicity);
        business.PreapprovalStatus = preapproval.Status;
        await _db.SaveChangesAsync(cancellationToken);

        return ToSummary(business, preapproval);
    }

    private static void ApplyPreapprovalToBusiness(
        Business business,
        string planTier,
        SubscriptionPeriodicity periodicity,
        MercadoPagoPreapproval preapproval,
        DateTime now,
        bool isNew)
    {
        business.PlanTier = planTier;
        business.PreapprovalId = preapproval.Id;
        business.PayerEmail = preapproval.PayerEmail ?? business.PayerEmail;
        business.PreapprovalStatus = preapproval.Status;
        business.SubscriptionStatus = SubscriptionStatus.Active;
        business.TrialEndsAt = null;
        business.CancellationEffectiveAt = null;
        business.PendingPlanTier = null;
        business.PendingPlanEffectiveAt = null;
        ApplyPeriodicity(business, periodicity);
        business.CurrentPeriodEndsAt = preapproval.NextPaymentDate
            ?? ComputeNextPeriodEnd(now, periodicity);
    }

    private static void ApplyPeriodicity(Business business, SubscriptionPeriodicity periodicity)
    {
        business.SubscriptionPeriodMonths = (int)periodicity;
    }

    internal static DateTime ComputeNextPeriodEnd(
        DateTime fromUtc,
        SubscriptionPeriodicity periodicity)
    {
        return periodicity switch
        {
            SubscriptionPeriodicity.Quarterly => fromUtc.AddMonths(3),
            SubscriptionPeriodicity.Annual => fromUtc.AddYears(1),
            _ => fromUtc.AddMonths(1)
        };
    }

    internal static PreapprovalSummaryDto ToSummary(
        Business business,
        MercadoPagoPreapproval preapproval)
    {
        return new PreapprovalSummaryDto(
            preapproval.Id,
            business.PlanTier,
            MonthsToPeriodicity(business.SubscriptionPeriodMonths),
            preapproval.TransactionAmount,
            preapproval.CurrencyId,
            preapproval.Status,
            preapproval.NextPaymentDate,
            business.CurrentPeriodEndsAt,
            business.CancellationEffectiveAt,
            preapproval.InitPoint);
    }

    private static string MonthsToPeriodicity(int months)
    {
        return months switch
        {
            3 => SubscriptionPeriodicities.Quarterly,
            12 => SubscriptionPeriodicities.Annual,
            _ => SubscriptionPeriodicities.Monthly
        };
    }

    private decimal QuarterlyDiscountPct() =>
        ReadDiscount("Subscriptions:PeriodicityDiscounts:Quarterly", 10m);

    private decimal AnnualDiscountPct() =>
        ReadDiscount("Subscriptions:PeriodicityDiscounts:Annual", 20m);

    private decimal ReadDiscount(string key, decimal fallback)
    {
        var raw = HttpContext.RequestServices
            .GetService(typeof(IConfiguration)) is IConfiguration config
            && decimal.TryParse(
                config[key],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed)
            ? parsed
            : fallback;
        return raw;
    }
}
