using System.Net.Http;
using System.Security.Claims;
using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntregasApi.Tests;

public class BrandControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetMe_ReturnsBrandAndSubscriptionAndFeatures()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active,
            brandPrimaryColor: "#FF0072",
            logoUrl: "https://cdn/logo.png");

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var result = await controller.GetMe(default);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BusinessMeDto>(ok.Value);
        Assert.Equal(business.Id, dto.Id);
        Assert.Equal("Regi Bazar", dto.Name);
        Assert.Equal("regibazar", dto.Slug);
        Assert.Equal("#FF0072", dto.Brand.BrandPrimaryColor);
        Assert.Equal("https://cdn/logo.png", dto.Brand.LogoUrl);
        Assert.Null(dto.Brand.BannerUrl);
        Assert.Null(dto.Brand.BrandAccentColor);
        Assert.False(dto.Subscription.IsLocked);
        Assert.Equal(PlanTiers.Pro, dto.Subscription.EffectivePlan);
        Assert.NotEmpty(dto.Features);
        Assert.Contains("Financials", dto.Features);
    }

    [Fact]
    public async Task GetMe_OnLockedBusiness_DoesNotIncludeProFeatures()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Locked,
            status: SubscriptionStatus.Expired);

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: true));

        var result = await controller.GetMe(default);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BusinessMeDto>(ok.Value);
        Assert.True(dto.Subscription.IsLocked);
        Assert.Empty(dto.Features);
    }

    [Fact]
    public async Task UploadLogo_StoresUrlInTargetBusinessOnly()
    {
        using var ctx = TestDbContextFactory.Create();
        var target = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active, slug: "target");
        var other = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active, slug: "other");

        var cloudinary = new StubCloudinary();
        cloudinary.UploadResult = "https://cdn.target/logo.png";
        var controller = BuildController(ctx, target.Id, cloudinary,
            new FakeEntitlementService(isLocked: false));

        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var file = new FormFile(stream, 0, stream.Length, "logo", "logo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.UploadLogo(file, default);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BrandAssetDto>(ok.Value);
        Assert.Equal("logo", dto.Kind);
        Assert.Equal("https://cdn.target/logo.png", dto.Url);

        Assert.Equal("brand", cloudinary.LastFolder);
        // El RootFolder por tenant lo construye el CloudinaryService real
        // a partir del slug del business activo. Aqui solo verificamos que
        // la URL se guardo en el business correcto.

        var stored = await ctx.Businesses.FindAsync(target.Id);
        var storedOther = await ctx.Businesses.FindAsync(other.Id);
        Assert.Equal("https://cdn.target/logo.png", stored!.LogoUrl);
        Assert.Null(storedOther!.LogoUrl);
    }

    [Fact]
    public async Task UploadLogo_RejectsFileAbove2Mb()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        // 2 MB + 1 byte
        var bytes = new byte[(2 * 1024 * 1024) + 1];
        using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, stream.Length, "logo", "logo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.UploadLogo(file, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("2MB", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task UploadBanner_RejectsNonImageFile()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        using var stream = new MemoryStream("hello world"u8.ToArray());
        var file = new FormFile(stream, 0, stream.Length, "banner", "banner.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };

        var result = await controller.UploadBanner(file, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("png", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task UploadLogo_RejectsEmptyFile()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var result = await controller.UploadLogo(file: null, default);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateBrand_ChangesNameAndColor()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active,
            brandPrimaryColor: "#6C4AE0");

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var result = await controller.UpdateBrand(new UpdateBrandRequest(
            Name: "  Tienda Regi  ",
            BrandPrimaryColor: "#ff0072",
            BrandAccentColor: "#000000"), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BrandDto>(ok.Value);
        Assert.Equal("Tienda Regi", dto.BrandPrimaryColor == "#FF0072" ? "Tienda Regi" : dto.BrandPrimaryColor);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal("Tienda Regi", stored!.Name);
        Assert.Equal("#FF0072", stored.BrandPrimaryColor);
        Assert.Equal("#000000", stored.BrandAccentColor);
    }

    [Fact]
    public async Task UpdateBrand_RejectsInvalidHexColor()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var result = await controller.UpdateBrand(new UpdateBrandRequest(
            BrandPrimaryColor: "rojo"), default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("hex", badRequest.Value!.ToString());

        // No se persistio nada
        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal("#6C4AE0", stored!.BrandPrimaryColor);
    }

    [Fact]
    public async Task UpdateBrand_AllowsClearingAccentColor()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);
        business.BrandAccentColor = "#FFAA00";
        await ctx.SaveChangesAsync();

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var result = await controller.UpdateBrand(new UpdateBrandRequest(
            BrandAccentColor: ""), default);

        Assert.IsType<OkObjectResult>(result.Result);
        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Null(stored!.BrandAccentColor);
    }

    [Fact]
    public async Task UpdateBrand_RejectsNameTooLong()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var result = await controller.UpdateBrand(new UpdateBrandRequest(
            Name: new string('a', 200)), default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("150", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task PaymentSettings_AreScopedToActiveBusiness_AndDoNotExposeAccessToken()
    {
        using var ctx = TestDbContextFactory.Create();
        var target = await SeedAsync(ctx, PlanTiers.Pro, SubscriptionStatus.Active, slug: "target");
        var other = await SeedAsync(ctx, PlanTiers.Pro, SubscriptionStatus.Active, slug: "other");
        other.MercadoPagoPublicKey = "OTHER-PUBLIC";
        other.MercadoPagoAccessToken = "OTHER-SECRET";
        await ctx.SaveChangesAsync();

        var controller = BuildController(ctx, target.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var update = await controller.UpdatePaymentSettings(
            new UpdateMercadoPagoPaymentSettingsRequest(
                PublicKey: "TARGET-PUBLIC",
                AccessToken: "TARGET-SECRET"),
            default);

        var ok = Assert.IsType<OkObjectResult>(update.Result);
        var dto = Assert.IsType<MercadoPagoPaymentSettingsDto>(ok.Value);
        Assert.Equal("TARGET-PUBLIC", dto.PublicKey);
        Assert.True(dto.HasAccessToken);
        Assert.True(dto.IsConfigured);
        Assert.DoesNotContain("TARGET-SECRET", dto.ToString());

        var storedTarget = await ctx.Businesses.FindAsync(target.Id);
        var storedOther = await ctx.Businesses.FindAsync(other.Id);
        Assert.Equal("TARGET-SECRET", storedTarget!.MercadoPagoAccessToken);
        Assert.Equal("TARGET-PUBLIC", storedTarget.MercadoPagoPublicKey);
        Assert.Equal("OTHER-SECRET", storedOther!.MercadoPagoAccessToken);
        Assert.Equal("OTHER-PUBLIC", storedOther.MercadoPagoPublicKey);
    }

    [Fact]
    public async Task PaymentSettings_CanClearCredentials()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, PlanTiers.Pro, SubscriptionStatus.Active);
        business.MercadoPagoPublicKey = "PUBLIC";
        business.MercadoPagoAccessToken = "SECRET";
        await ctx.SaveChangesAsync();

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var update = await controller.UpdatePaymentSettings(
            new UpdateMercadoPagoPaymentSettingsRequest(
                PublicKey: "",
                ClearAccessToken: true),
            default);

        var ok = Assert.IsType<OkObjectResult>(update.Result);
        var dto = Assert.IsType<MercadoPagoPaymentSettingsDto>(ok.Value);
        Assert.Null(dto.PublicKey);
        Assert.False(dto.HasAccessToken);
        Assert.False(dto.IsConfigured);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Null(stored!.MercadoPagoPublicKey);
        Assert.Null(stored.MercadoPagoAccessToken);
    }

    [Fact]
    public void TryValidateHexColor_AcceptsAndNormalizesHex()
    {
        Assert.True(BrandController.TryValidateHexColor("#ff0072", out var n1));
        Assert.Equal("#FF0072", n1);

        Assert.True(BrandController.TryValidateHexColor("#ABCDEF", out var n2));
        Assert.Equal("#ABCDEF", n2);

        Assert.True(BrandController.TryValidateHexColor("  #123456  ", out var n3));
        Assert.Equal("#123456", n3);
    }

    [Theory]
    [InlineData("FF0072")]      // sin #
    [InlineData("#FFF")]        // 3 digitos
    [InlineData("#1234567")]    // 7 digitos
    [InlineData("#ZZZZZZ")]     // no hex
    [InlineData("")]
    [InlineData(null)]
    public void TryValidateHexColor_RejectsBadValues(string? value)
    {
        var ok = BrandController.TryValidateHexColor(value, out var normalized);
        Assert.False(ok);
        Assert.Null(normalized);
    }

    [Fact]
    public async Task GetMe_UsesSeededColorOnDefaultBusiness()
    {
        // Cubre el caso del seeder de DEV: el rosa de Regi Bazar.
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, planTier: PlanTiers.Elite,
            status: SubscriptionStatus.Active,
            brandPrimaryColor: "#FF0072");

        var controller = BuildController(ctx, business.Id, new StubCloudinary(),
            new FakeEntitlementService(isLocked: false));

        var result = await controller.GetMe(default);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BusinessMeDto>(ok.Value);
        Assert.Equal("#FF0072", dto.Brand.BrandPrimaryColor);
    }

    private static BrandController BuildController(
        AppDbContext db,
        int businessId,
        ICloudinaryService cloudinary,
        IEntitlementService entitlements)
    {
        var tenant = new TestCurrentTenant(businessId);
        var currentBusiness = new CurrentBusiness(db, tenant);

        var controller = new BrandController(
            db, tenant, currentBusiness, cloudinary, entitlements,
            NullLogger<BrandController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    private static async Task<Business> SeedAsync(
        AppDbContext ctx,
        string planTier,
        SubscriptionStatus status,
        string slug = "regibazar",
        string brandPrimaryColor = "#6C4AE0",
        string? logoUrl = null)
    {
        var business = new Business
        {
            Name = "Regi Bazar",
            Slug = slug,
            City = "Nuevo Laredo",
            FrontendUrl = "https://regibazar.com",
            BrandPrimaryColor = brandPrimaryColor,
            LogoUrl = logoUrl,
            DepotLat = 27.4861,
            DepotLng = -99.5069,
            GeocodingRegion = "Test, MX",
            GeminiBusinessName = "Regi Bazar",
            PlanTier = planTier,
            SubscriptionStatus = status,
            IsActive = true,
            CreatedAt = Now.UtcDateTime
        };
        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();
        return business;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(int businessId) { ActiveBusinessId = businessId; }
        public int ActiveBusinessId { get; private set; }
        public bool IsResolved => true;
        public void SetBusiness(int businessId) => ActiveBusinessId = businessId;
    }

    private sealed class StubCloudinary : ICloudinaryService
    {
        public string UploadResult { get; set; } = "https://cdn/asset.png";
        public string? LastFolder { get; private set; }
        public string? LastRootFolder { get; private set; }

        public Task<string> UploadAsync(Stream stream, string fileName, string folder)
        {
            LastFolder = folder;
            // El RootFolder lo construye el service real a partir del slug del
            // tenant activo; aqui no lo replicamos porque el test verifica
            // que la URL cae en el Business correcto, no el folder exacto.
            return Task.FromResult(UploadResult);
        }
    }

    private sealed class FakeEntitlementService : IEntitlementService
    {
        private readonly bool _isLocked;
        public FakeEntitlementService(bool isLocked) => _isLocked = isLocked;

        public Task<bool> HasFeatureAsync(Feature feature, CancellationToken cancellationToken = default)
            => Task.FromResult(!_isLocked);

        public Task<int> GetLimitAsync(LimitKey limitKey, CancellationToken cancellationToken = default)
            => Task.FromResult(_isLocked ? 0 : int.MaxValue);

        public Task<string> EffectivePlanTierAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_isLocked ? PlanTiers.Locked : PlanTiers.Pro);

        public Task<SubscriptionSnapshot> GetSubscriptionSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SubscriptionSnapshot(
                _isLocked ? PlanTiers.Locked : PlanTiers.Pro,
                PlanTiers.Pro,
                _isLocked ? SubscriptionStatus.Expired : SubscriptionStatus.Active,
                null, null, null, null,
                _isLocked, 0, 3));
        }

        public Task EnsureWithinLimitAsync(LimitKey limitKey, int currentCount, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Feature>> GetEnabledFeaturesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Feature>>(_isLocked
                ? Array.Empty<Feature>()
                : new[] { Feature.ManualOrders, Feature.ClientDirectory, Feature.Financials, Feature.LivePush });
    }
}
