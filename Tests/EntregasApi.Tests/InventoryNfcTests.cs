using System.Security.Claims;
using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntregasApi.Tests;

public class InventoryNfcTests
{
    [Fact]
    public async Task Box_NfcBindingAndInitialStock_AreScopedAndAudited()
    {
        await using var db = TestDbContextFactory.Create();
        db.Businesses.Add(new Business { Id = 1, Name = "Tienda Nenis", Slug = "tienda-nenis" });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var create = await controller.CreateBox(
            new CreateInventoryBoxDto("B-01", "Blusas", "Estante A"),
            CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(create.Result);
        var box = Assert.IsType<InventoryBoxDto>(created.Value);

        Assert.StartsWith("https://app.nenisapp.com/caja/1/", box.NfcUrl);
        Assert.False(box.IsNfcBound);

        var binding = await controller.BindNfc(
            box.Id,
            new BindInventoryNfcDto("04-A1-B2-C3"),
            CancellationToken.None);
        var bound = Assert.IsType<OkObjectResult>(binding.Result);
        var boundBox = Assert.IsType<InventoryBoxDto>(bound.Value);
        Assert.True(boundBox.IsNfcBound);

        var added = await controller.AddItem(
            box.Id,
            new CreateInventoryItemDto("Blusa satinada", "Rosa · M", "7501234567890", 4, "Inventario inicial"),
            CancellationToken.None);
        var result = Assert.IsType<OkObjectResult>(added.Result);
        var stocked = Assert.IsType<InventoryBoxDto>(result.Value);

        var item = Assert.Single(stocked.Items);
        Assert.StartsWith("NNI", item.LabelCode);
        Assert.Equal(4, item.Quantity);
        var movement = Assert.Single(stocked.Movements);
        Assert.Equal("InitialCount", movement.Type);
        Assert.Equal(4, movement.QuantityDelta);
    }

    [Fact]
    public async Task TransferAndPhysicalCount_CreateACompleteAuditTrail()
    {
        await using var db = TestDbContextFactory.Create();
        db.Businesses.Add(new Business { Id = 1, Name = "Tienda Nenis", Slug = "tienda-nenis" });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var source = await CreateBoxAsync(controller, "B-01", "Origen");
        var destination = await CreateBoxAsync(controller, "B-02", "Destino");
        var added = await controller.AddItem(
            source.Id,
            new CreateInventoryItemDto("Vestido", null, null, 5, null),
            CancellationToken.None);
        var sourceWithItem = Assert.IsType<InventoryBoxDto>(Assert.IsType<OkObjectResult>(added.Result).Value);
        var sourceItem = Assert.Single(sourceWithItem.Items);

        var transfer = await controller.Transfer(
            new TransferInventoryItemsDto(source.Id, destination.Id, sourceItem.Id, 2, "Cambio de estante"),
            CancellationToken.None);
        var destinationAfterTransfer = Assert.IsType<InventoryBoxDto>(Assert.IsType<OkObjectResult>(transfer.Result).Value);
        Assert.Equal(2, Assert.Single(destinationAfterTransfer.Items).Quantity);

        var count = await controller.CompleteCount(
            destination.Id,
            new CompleteInventoryCountDto(
                [new InventoryCountItemDto(destinationAfterTransfer.Items[0].Id, 1)],
                "Conteo de cierre"),
            CancellationToken.None);
        var counted = Assert.IsType<InventoryBoxDto>(Assert.IsType<OkObjectResult>(count.Result).Value);
        Assert.Equal(1, Assert.Single(counted.Items).Quantity);
        Assert.Contains(counted.Movements, movement =>
            movement.Type == "CountAdjustment" && movement.QuantityDelta == -1);

        var transfers = await db.InventoryMovements
            .IgnoreQueryFilters()
            .Where(movement => movement.TransferGroupId != null)
            .ToListAsync();
        Assert.Equal(2, transfers.Count);
        Assert.Single(transfers.Select(movement => movement.TransferGroupId).Distinct());
    }

    [Fact]
    public async Task InventoryLabelPrint_FreezesThePublishedTemplateAndBoxData()
    {
        await using var db = TestDbContextFactory.Create();
        db.Businesses.Add(new Business { Id = 1, Name = "Tienda Nenis", Slug = "tienda-nenis" });
        await db.SaveChangesAsync();
        var controller = CreateController(db);
        var box = await CreateBoxAsync(controller, "B-08", "Accesorios");

        var created = await controller.CreateLabelPrint(
            new CreateInventoryLabelPrintDto("InventoryBox", box.Id, "Square50x50", 2),
            CancellationToken.None);
        var result = Assert.IsType<CreatedAtActionResult>(created.Result);
        var print = Assert.IsType<InventoryLabelPrintDto>(result.Value);

        Assert.Equal("Prepared", print.Status);
        Assert.Equal(2, print.Copies);
        Assert.Equal("B-08", print.Data["box.code"]);
        Assert.Contains("/caja/1/", print.Data["box.nfcUrl"], StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, print.TemplateVersion.Id);
        Assert.Single(await db.InventoryLabelPrints.ToListAsync());
    }

    private static async Task<InventoryBoxDto> CreateBoxAsync(
        InventoryController controller,
        string code,
        string name)
    {
        var result = await controller.CreateBox(
            new CreateInventoryBoxDto(code, name, null),
            CancellationToken.None);
        return Assert.IsType<InventoryBoxDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
    }

    private static InventoryController CreateController(AppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:InventoryLinkBaseUrl"] = "https://app.nenisapp.com"
            })
            .Build();
        var controller = new InventoryController(
            db,
            configuration,
            new LabelTemplateCatalogService(db),
            new LabelTemplateDesignValidator())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "Ana")],
                        "test"))
                }
            }
        };
        return controller;
    }
}
