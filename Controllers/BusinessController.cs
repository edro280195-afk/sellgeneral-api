using System.Globalization;
using System.Text;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/business")]
public class BusinessController : ControllerBase
{
    private const double DefaultDepotLat = 27.4861;
    private const double DefaultDepotLng = -99.5069;
    private const string DefaultGeocodingRegion = "Nuevo Laredo, Tamaulipas, MX";

    private readonly AppDbContext _db;
    private readonly ICurrentAccount _currentAccount;
    private readonly ICurrentTenant _currentTenant;
    private readonly IEntitlementService _entitlements;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public BusinessController(
        AppDbContext db,
        ICurrentAccount currentAccount,
        ICurrentTenant currentTenant,
        IEntitlementService entitlements,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentAccount = currentAccount;
        _currentTenant = currentTenant;
        _entitlements = entitlements;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    [HttpPost]
    [SkipTenantResolution]
    [Authorize(
        Policy = AuthorizationPolicies.AuthenticatedAccount,
        AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<BusinessOnboardingResponse>> CreateBusiness(
        [FromBody] CreateBusinessRequest request,
        CancellationToken cancellationToken)
    {
        if (_currentAccount.AccountId is not int accountId)
        {
            return Unauthorized(new { message = "La cuenta no esta autenticada." });
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "El nombre del negocio es obligatorio." });
        }

        if (name.Length > 150)
        {
            return BadRequest(new { message = "El nombre del negocio no puede exceder 150 caracteres." });
        }

        var baseSlug = Slugify(string.IsNullOrWhiteSpace(request.Slug) ? name : request.Slug!);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            return BadRequest(new { message = "El slug del negocio no es valido." });
        }

        var depotLat = request.DepotLat ?? _configuration.GetValue<double?>("Cami:RouteCenterLat") ?? DefaultDepotLat;
        var depotLng = request.DepotLng ?? _configuration.GetValue<double?>("Cami:RouteCenterLng") ?? DefaultDepotLng;
        if (depotLat is < -90 or > 90 || depotLng is < -180 or > 180)
        {
            return BadRequest(new { message = "Las coordenadas del negocio no son validas." });
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var slug = await GenerateUniqueSlugAsync(baseSlug, cancellationToken);
        var business = new Business
        {
            Name = name,
            Slug = slug,
            City = NormalizeOptional(request.City, 120),
            FrontendUrl = NormalizeOptional(request.FrontendUrl, 300),
            DepotLat = depotLat,
            DepotLng = depotLng,
            GeocodingRegion = NormalizeOptional(request.GeocodingRegion, 120) ?? DefaultGeocodingRegion,
            GeminiBusinessName = name,
            PlanTier = PlanTiers.Pro,
            SubscriptionStatus = SubscriptionStatus.Trialing,
            TrialEndsAt = now.AddDays(14),
            CurrentPeriodEndsAt = null,
            IsActive = true,
            CreatedAt = now
        };

        var membership = new Membership
        {
            AccountId = accountId,
            Business = business,
            Role = MembershipRole.Owner,
            CreatedAt = now
        };

        _db.Businesses.Add(business);
        _db.Memberships.Add(membership);
        await _db.SaveChangesAsync(cancellationToken);

        _currentTenant.SetBusiness(business.Id);
        var state = await BuildCurrentStateDtoAsync(cancellationToken);
        var response = new BusinessOnboardingResponse(
            business.Id,
            business.Name,
            business.Slug,
            MembershipRole.Owner.ToString(),
            state);

        return Created($"/api/business/{business.Id}", response);
    }

    [HttpGet("account-status")]
    [HttpGet("subscription/status")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<ActionResult<SubscriptionAccountStateDto>> GetAccountStatus(
        CancellationToken cancellationToken)
    {
        return Ok(await BuildCurrentStateDtoAsync(cancellationToken));
    }

    [HttpPut("subscription/plan")]
    [HttpPost("subscription/plan")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<ActionResult<SubscriptionAccountStateDto>> ChangePlan(
        [FromBody] ChangePlanRequest request,
        CancellationToken cancellationToken)
    {
        if (!PlanCatalog.TryNormalizeSelectablePlan(request.PlanTier, out var targetPlanTier))
        {
            return BadRequest(new
            {
                message = "Plan invalido. Usa Entrada, Pro o Elite."
            });
        }

        var business = await _db.Businesses
            .FirstOrDefaultAsync(b => b.Id == _currentTenant.ActiveBusinessId, cancellationToken);

        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var currentRank = PlanCatalog.GetRank(business.PlanTier);
        var targetRank = PlanCatalog.GetRank(targetPlanTier);
        var isDowngradeWithFuturePeriod =
            targetRank < currentRank &&
            business.CurrentPeriodEndsAt is not null &&
            business.CurrentPeriodEndsAt > now;

        if (isDowngradeWithFuturePeriod)
        {
            business.PendingPlanTier = targetPlanTier;
            business.PendingPlanEffectiveAt = business.CurrentPeriodEndsAt;
        }
        else
        {
            business.PlanTier = targetPlanTier;
            business.PendingPlanTier = null;
            business.PendingPlanEffectiveAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await BuildCurrentStateDtoAsync(cancellationToken));
    }

    private async Task<SubscriptionAccountStateDto> BuildCurrentStateDtoAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await _entitlements.GetSubscriptionSnapshotAsync(cancellationToken);
        return new SubscriptionAccountStateDto(
            snapshot.EffectivePlanTier,
            snapshot.PlanTier,
            snapshot.SubscriptionStatus.ToString(),
            snapshot.TrialEndsAt,
            snapshot.CurrentPeriodEndsAt,
            snapshot.PendingPlanTier,
            snapshot.PendingPlanEffectiveAt,
            snapshot.IsLocked,
            snapshot.DaysLeft,
            snapshot.PastDueGraceDays);
    }

    private async Task<string> GenerateUniqueSlugAsync(
        string baseSlug,
        CancellationToken cancellationToken)
    {
        baseSlug = baseSlug.Length > 50 ? baseSlug[..50].Trim('-') : baseSlug;
        var slug = baseSlug;
        var suffix = 2;

        while (await _db.Businesses.AnyAsync(b => b.Slug == slug, cancellationToken))
        {
            var suffixText = $"-{suffix++}";
            var maxBaseLength = Math.Max(1, 60 - suffixText.Length);
            var trimmedBase = baseSlug.Length > maxBaseLength
                ? baseSlug[..maxBaseLength].Trim('-')
                : baseSlug;
            slug = $"{trimmedBase}{suffixText}";
        }

        return slug;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static string Slugify(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder(normalized.Length);
        var lastWasSeparator = false;

        foreach (var rawChar in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(rawChar);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var c = char.ToLowerInvariant(rawChar);
            if (c <= 127 && char.IsLetterOrDigit(c))
            {
                slug.Append(c);
                lastWasSeparator = false;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is '-' or '_')
            {
                if (!lastWasSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                    lastWasSeparator = true;
                }
            }
        }

        return slug.ToString().Trim('-');
    }
}
