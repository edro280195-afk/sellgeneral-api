using System.Text.RegularExpressions;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/business")]
public class BrandController : ControllerBase
{
    private const long MaxLogoBytes = 2L * 1024 * 1024;
    private const long MaxBannerBytes = 5L * 1024 * 1024;
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/webp"
    };

    private static readonly Regex HexColorRegex = new(
        "^#(?<r>[0-9A-Fa-f]{2})(?<g>[0-9A-Fa-f]{2})(?<b>[0-9A-Fa-f]{2})$",
        RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentBusiness _currentBusiness;
    private readonly ICloudinaryService _cloudinary;
    private readonly IEntitlementService _entitlements;
    private readonly ILogger<BrandController> _logger;

    public BrandController(
        AppDbContext db,
        ICurrentTenant currentTenant,
        ICurrentBusiness currentBusiness,
        ICloudinaryService cloudinary,
        IEntitlementService entitlements,
        ILogger<BrandController> logger)
    {
        _db = db;
        _currentTenant = currentTenant;
        _currentBusiness = currentBusiness;
        _cloudinary = cloudinary;
        _entitlements = entitlements;
        _logger = logger;
    }

    /// <summary>
    /// Bootstrap que el frontend llama al cargar: en UNA llamada devuelve
    /// marca + estado de suscripcion + features habilitadas del plan
    /// efectivo. Asi el panel se tematiza sin hacer 3+ requests.
    /// </summary>
    [HttpGet("me")]
    [BypassSubscriptionLock]
    [Authorize(
        Policy = AuthorizationPolicies.BusinessMember,
        AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<BusinessMeDto>> GetMe(CancellationToken cancellationToken)
    {
        var business = await LoadActiveBusinessAsync(cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        var snapshot = await _entitlements.GetSubscriptionSnapshotAsync(cancellationToken);
        var features = await _entitlements.GetEnabledFeaturesAsync(cancellationToken);

        return Ok(new BusinessMeDto(
            business.Id,
            business.Name,
            business.Slug,
            business.City,
            new BrandDto(
                business.LogoUrl,
                business.BannerUrl,
                business.BrandPrimaryColor,
                business.BrandAccentColor,
                business.MessengerUrl,
                business.FacebookUrl),
            new SubscriptionSummaryDto(
                snapshot.EffectivePlanTier,
                snapshot.SubscriptionStatus.ToString(),
                snapshot.TrialEndsAt,
                snapshot.CurrentPeriodEndsAt,
                snapshot.IsLocked,
                snapshot.DaysLeft,
                snapshot.PastDueGraceDays),
            features.Select(f => f.ToString()).ToList()));
    }

    /// <summary>Sube el logo del negocio a Cloudinary (carpeta "{slug}/brand").</summary>
    [HttpPost("brand/logo")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [RequestSizeLimit(MaxLogoBytes)]
    public async Task<ActionResult<BrandAssetDto>> UploadLogo(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        return await UploadAsync(file, BrandAssetKind.Logo, cancellationToken);
    }

    /// <summary>Sube el banner del negocio a Cloudinary (carpeta "{slug}/brand").</summary>
    [HttpPost("brand/banner")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [RequestSizeLimit(MaxBannerBytes)]
    public async Task<ActionResult<BrandAssetDto>> UploadBanner(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        return await UploadAsync(file, BrandAssetKind.Banner, cancellationToken);
    }

    /// <summary>Edita el nombre del negocio y los colores de la marca.</summary>
    [HttpPut("brand")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<ActionResult<BrandDto>> UpdateBrand(
        [FromBody] UpdateBrandRequest request,
        CancellationToken cancellationToken)
    {
        var business = await LoadActiveBusinessAsync(cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { message = "El nombre no puede estar vacio." });
            }
            if (name.Length > 150)
            {
                return BadRequest(new { message = "El nombre no puede exceder 150 caracteres." });
            }
            business.Name = name;
        }

        if (request.BrandPrimaryColor is not null)
        {
            if (!TryValidateHexColor(request.BrandPrimaryColor, out var normalized))
            {
                return BadRequest(new { message = "brandPrimaryColor debe ser un color hex \"#RRGGBB\"." });
            }
            business.BrandPrimaryColor = normalized!;
        }

        if (request.BrandAccentColor is not null)
        {
            if (string.IsNullOrWhiteSpace(request.BrandAccentColor))
            {
                business.BrandAccentColor = null;
            }
            else if (!TryValidateHexColor(request.BrandAccentColor, out var normalized))
            {
                return BadRequest(new { message = "brandAccentColor debe ser un color hex \"#RRGGBB\" o null." });
            }
            else
            {
                business.BrandAccentColor = normalized!;
            }
        }

        if (request.MessengerUrl is not null)
        {
            var url = request.MessengerUrl.Trim();
            if (url.Length > 300)
            {
                return BadRequest(new { message = "messengerUrl no puede exceder 300 caracteres." });
            }
            business.MessengerUrl = string.IsNullOrWhiteSpace(url) ? null : url;
        }

        if (request.FacebookUrl is not null)
        {
            var url = request.FacebookUrl.Trim();
            if (url.Length > 300)
            {
                return BadRequest(new { message = "facebookUrl no puede exceder 300 caracteres." });
            }
            business.FacebookUrl = string.IsNullOrWhiteSpace(url) ? null : url;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new BrandDto(
            business.LogoUrl,
            business.BannerUrl,
            business.BrandPrimaryColor,
            business.BrandAccentColor,
            business.MessengerUrl,
            business.FacebookUrl));
    }

    [HttpGet("payment-settings")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<ActionResult<MercadoPagoPaymentSettingsDto>> GetPaymentSettings(
        CancellationToken cancellationToken)
    {
        var business = await LoadActiveBusinessAsync(cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        return Ok(ToPaymentSettingsDto(business));
    }

    [HttpPut("payment-settings")]
    [BypassSubscriptionLock]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<ActionResult<MercadoPagoPaymentSettingsDto>> UpdatePaymentSettings(
        [FromBody] UpdateMercadoPagoPaymentSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var business = await LoadActiveBusinessAsync(cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        var publicKey = request.PublicKey?.Trim();
        if (publicKey?.Length > 200)
        {
            return BadRequest(new { message = "La Public Key no puede exceder 200 caracteres." });
        }

        if (!string.IsNullOrWhiteSpace(publicKey) && publicKey.Any(char.IsWhiteSpace))
        {
            return BadRequest(new { message = "La Public Key no puede contener espacios." });
        }

        var accessToken = request.AccessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            if (accessToken.Length > 500)
            {
                return BadRequest(new { message = "El Access Token no puede exceder 500 caracteres." });
            }

            if (accessToken.Any(char.IsWhiteSpace))
            {
                return BadRequest(new { message = "El Access Token no puede contener espacios." });
            }
        }

        business.MercadoPagoPublicKey = string.IsNullOrWhiteSpace(publicKey) ? null : publicKey;

        if (request.ClearAccessToken)
        {
            business.MercadoPagoAccessToken = null;
        }
        else if (!string.IsNullOrWhiteSpace(accessToken))
        {
            business.MercadoPagoAccessToken = accessToken;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(ToPaymentSettingsDto(business));
    }

    private async Task<ActionResult<BrandAssetDto>> UploadAsync(
        IFormFile? file,
        BrandAssetKind kind,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "No se recibio el archivo." });
        }

        var maxBytes = kind == BrandAssetKind.Logo ? MaxLogoBytes : MaxBannerBytes;
        if (file.Length > maxBytes)
        {
            return BadRequest(new
            {
                message = kind == BrandAssetKind.Logo
                    ? "El logo excede 2MB."
                    : "El banner excede 5MB."
            });
        }

        if (!AllowedImageContentTypes.Contains(file.ContentType ?? string.Empty))
        {
            return BadRequest(new
            {
                message = "Tipo de archivo invalido. Solo png, jpg o webp."
            });
        }

        var business = await LoadActiveBusinessAsync(cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        string url;
        try
        {
            using var stream = file.OpenReadStream();
            url = await _cloudinary.UploadAsync(stream, file.FileName, "brand");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Brand] Fallo la subida de {Kind} para Business {Id}", kind, business.Id);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "No se pudo subir la imagen. Intenta de nuevo."
            });
        }

        if (kind == BrandAssetKind.Logo)
        {
            business.LogoUrl = url;
        }
        else
        {
            business.BannerUrl = url;
        }
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new BrandAssetDto(kind.ToString().ToLowerInvariant(), url));
    }

    private async Task<Business?> LoadActiveBusinessAsync(CancellationToken cancellationToken)
    {
        var businessId = _currentTenant.ActiveBusinessId;
        if (businessId == 0)
        {
            return null;
        }

        return await _db.Businesses
            .FirstOrDefaultAsync(b => b.Id == businessId, cancellationToken);
    }

    internal static bool TryValidateHexColor(string value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var match = HexColorRegex.Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        normalized = $"#{match.Groups["r"].Value.ToUpperInvariant()}" +
                     $"{match.Groups["g"].Value.ToUpperInvariant()}" +
                     $"{match.Groups["b"].Value.ToUpperInvariant()}";
        return true;
    }

    private static MercadoPagoPaymentSettingsDto ToPaymentSettingsDto(Business business)
    {
        var hasAccessToken = !string.IsNullOrWhiteSpace(business.MercadoPagoAccessToken);
        var publicKey = string.IsNullOrWhiteSpace(business.MercadoPagoPublicKey)
            ? null
            : business.MercadoPagoPublicKey;

        return new MercadoPagoPaymentSettingsDto(
            publicKey,
            hasAccessToken,
            hasAccessToken && publicKey is not null);
    }

    private enum BrandAssetKind
    {
        Logo,
        Banner
    }
}
