using EntregasApi.Models;
using EntregasApi.Services;
using EntregasApi.Controllers;
using EntregasApi.DTOs;
using EntregasApi.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace EntregasApi.Tests;

public class LabelingFoundationTests
{
    private readonly LabelTemplateDesignValidator _validator = new();

    [Theory]
    [InlineData(LabelMediaSize.Shipping4x6)]
    [InlineData(LabelMediaSize.Square50x50)]
    public void DefaultOrderPackageDesign_IsValidAndKeepsRequiredData(LabelMediaSize mediaSize)
    {
        var design = LabelTemplateDesignFactory.CreateDefaultDesign(LabelTemplateKind.OrderPackage, mediaSize);

        var result = _validator.Validate(design, LabelTemplateKind.OrderPackage, mediaSize);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void PackageDesign_WithoutPackageQr_IsRejected()
    {
        var design = LabelTemplateDesignFactory
            .CreateDefaultDesign(LabelTemplateKind.OrderPackage, LabelMediaSize.Shipping4x6)
            .Replace("package.qrCodeValue", "package.alternateCode", StringComparison.Ordinal);

        var result = _validator.Validate(design, LabelTemplateKind.OrderPackage, LabelMediaSize.Shipping4x6);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("package.qrCodeValue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalog_CreatesOnePublishedDefaultAndOneDraftForTheStore()
    {
        await using var db = TestDbContextFactory.Create();
        var catalog = new LabelTemplateCatalogService(db);

        var first = await catalog.GetOrCreateDefaultOrderPackageTemplateAsync(
            LabelMediaSize.Shipping4x6,
            "Ana",
            CancellationToken.None);
        var second = await catalog.GetOrCreateDefaultOrderPackageTemplateAsync(
            LabelMediaSize.Shipping4x6,
            "Ana",
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.True(first.IsDefault);
        Assert.NotNull(first.PublishedVersionId);
        Assert.Equal(2, await db.LabelTemplateVersions.CountAsync());
        Assert.Single(await db.LabelTemplateVersions.Where(version => version.Status == LabelTemplateVersionStatus.Published).ToListAsync());
        Assert.Single(await db.LabelTemplateVersions.Where(version => version.Status == LabelTemplateVersionStatus.Draft).ToListAsync());
    }

    [Fact]
    public void LabelPrinting_IsRestrictedToProAndElite()
    {
        Assert.DoesNotContain(Feature.LabelPrinting, PlanCatalog.Get(PlanTiers.Entrada).Features);
        Assert.Contains(Feature.LabelPrinting, PlanCatalog.Get(PlanTiers.Pro).Features);
        Assert.Contains(Feature.LabelPrinting, PlanCatalog.Get(PlanTiers.Elite).Features);
        Assert.Equal(PlanTiers.Pro, PlanCatalog.GetRequiredPlan(Feature.LabelPrinting));
    }

    [Fact]
    public async Task CreateJob_SnapshotsPackageDataAndHandsItToTheSystem()
    {
        await using var db = TestDbContextFactory.Create();
        var business = new Business { Id = 1, Name = "Boutique Ana", Slug = "boutique-ana" };
        var client = new Client
        {
            BusinessId = 1,
            Name = "Mariana López",
            Phone = "8680000000",
            Address = "Calle Rosas 12",
            NormalizedName = "MARIANA LOPEZ"
        };
        var order = new Order
        {
            BusinessId = 1,
            Client = client,
            AccessToken = "pedido-prueba",
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Items = [new OrderItem { BusinessId = 1, ProductName = "Blusa lila", Quantity = 2, UnitPrice = 120, LineTotal = 240 }]
        };
        var package = new OrderPackage
        {
            BusinessId = 1,
            Order = order,
            PackageNumber = 1,
            QrCodeValue = "NN-ORD1-PKG1"
        };
        db.AddRange(business, client, order, package);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var created = await controller.Create(
            new CreateLabelPrintJobRequest([package.Id], "Shipping4x6"),
            CancellationToken.None);

        var createdAt = Assert.IsType<CreatedAtActionResult>(created.Result);
        var job = Assert.IsType<LabelPrintJobDto>(createdAt.Value);
        Assert.Equal("Prepared", job.Status);
        Assert.Single(job.Items);
        Assert.Equal("Boutique Ana", job.Items[0].Payload.BusinessName);
        Assert.Equal("Mariana López", job.Items[0].Payload.Order.ClientName);
        Assert.Contains("Blusa lila", job.Items[0].Payload.Order.ItemSummary, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, job.TemplateVersion.Id);

        var updated = await controller.UpdateStatus(
            job.Id,
            new UpdateLabelPrintJobStatusRequest("SentToSystem"),
            CancellationToken.None);
        var updatedResult = Assert.IsType<OkObjectResult>(updated.Result);
        var sent = Assert.IsType<LabelPrintJobDto>(updatedResult.Value);
        Assert.Equal("SentToSystem", sent.Status);
        Assert.NotNull(sent.HandedOffAt);
    }

    [Fact]
    public async Task AvailablePackages_OnlyReturnsPackagesThatCanStillBePrepared()
    {
        await using var db = TestDbContextFactory.Create();
        var business = new Business { Id = 1, Name = "Boutique Ana", Slug = "boutique-ana" };
        var client = new Client { BusinessId = 1, Name = "Mariana", NormalizedName = "MARIANA" };
        var order = new Order { BusinessId = 1, Client = client, AccessToken = "pedido-disponible", ExpiresAt = DateTime.UtcNow.AddDays(30) };
        db.AddRange(
            business,
            client,
            order,
            new OrderPackage { BusinessId = 1, Order = order, PackageNumber = 1, QrCodeValue = "NN-ORD1-PKG1", Status = PackageTrackingStatus.Packed },
            new OrderPackage { BusinessId = 1, Order = order, PackageNumber = 2, QrCodeValue = "NN-ORD1-PKG2", Status = PackageTrackingStatus.Loaded },
            new OrderPackage { BusinessId = 1, Order = order, PackageNumber = 3, QrCodeValue = "NN-ORD1-PKG3", Status = PackageTrackingStatus.Delivered });
        await db.SaveChangesAsync();

        var response = await CreateController(db).GetAvailablePackages(100, CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var packages = Assert.IsType<List<AvailableLabelPackageDto>>(result.Value);
        Assert.Equal(2, packages.Count);
        Assert.All(packages, package => Assert.NotEqual("Delivered", package.Status));
        Assert.All(packages, package => Assert.Equal(3, package.TotalPackages));
    }

    [Fact]
    public async Task TemplateEditor_PublishesAnImmutableVersionAndCreatesTheNextDraft()
    {
        await using var db = TestDbContextFactory.Create();
        var controller = CreateTemplateController(db);

        var initialResponse = await controller.GetOrderPackageDefault(
            "Shipping4x6",
            CancellationToken.None);
        var initialResult = Assert.IsType<OkObjectResult>(initialResponse.Result);
        var initial = Assert.IsType<LabelTemplateEditorDto>(initialResult.Value);
        var originalDraft = initial.DraftVersion;

        var editedDesign = originalDraft.DesignJson.Replace(
            "ENTREGAR A",
            "PREPARAR PARA",
            StringComparison.Ordinal);
        var savedResponse = await controller.SaveDraft(
            initial.Id,
            new SaveLabelTemplateDraftRequest(editedDesign, originalDraft.Revision),
            CancellationToken.None);
        var savedResult = Assert.IsType<OkObjectResult>(savedResponse.Result);
        var saved = Assert.IsType<LabelTemplateEditorDto>(savedResult.Value);
        Assert.Equal(originalDraft.Revision + 1, saved.DraftVersion.Revision);

        var publishedResponse = await controller.Publish(initial.Id, CancellationToken.None);
        var publishedResult = Assert.IsType<OkObjectResult>(publishedResponse.Result);
        var published = Assert.IsType<LabelTemplateEditorDto>(publishedResult.Value);

        Assert.NotNull(published.PublishedVersion);
        Assert.Equal(originalDraft.VersionNumber, published.PublishedVersion!.VersionNumber);
        Assert.Equal(editedDesign, published.PublishedVersion.DesignJson);
        Assert.Equal(originalDraft.VersionNumber + 1, published.DraftVersion.VersionNumber);
        Assert.Equal("Draft", published.DraftVersion.Status);
        Assert.Equal(1, published.DraftVersion.Revision);
        Assert.Contains(published.History, version =>
            version.VersionNumber == originalDraft.VersionNumber && version.Status == "Published");
    }

    private LabelPrintJobsController CreateController(AppDbContext db)
    {
        var controller = new LabelPrintJobsController(
            db,
            new LabelTemplateCatalogService(db),
            _validator,
            new FakeTenant(1));
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Ana")], "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private LabelTemplatesController CreateTemplateController(AppDbContext db)
    {
        var controller = new LabelTemplatesController(
            db,
            new LabelTemplateCatalogService(db),
            _validator,
            new FakeCloudinary());
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Ana")], "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private sealed class FakeTenant(int businessId) : ICurrentTenant
    {
        public int ActiveBusinessId { get; private set; } = businessId;
        public bool IsResolved => true;
        public void SetBusiness(int value) => ActiveBusinessId = value;
    }

    private sealed class FakeCloudinary : ICloudinaryService
    {
        public Task<string> UploadAsync(Stream stream, string fileName, string folder) =>
            Task.FromResult("https://example.test/labels/" + fileName);
    }
}
