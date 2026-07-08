using EntregasApi.Controllers;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EntregasApi.Tests;

public class OrdersControllerTests
{
    [Fact]
    public async Task GetCaptureSettings_ReturnsConfiguredDefaultShippingCost()
    {
        using var ctx = TestDbContextFactory.Create();
        ctx.AppSettings.Add(new AppSettings
        {
            BusinessId = 1,
            DefaultShippingCost = 85m,
            LinkExpirationHours = 72,
        });
        await ctx.SaveChangesAsync();
        var controller = new OrdersController(
            ctx,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var result = await controller.GetCaptureSettings();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var settings = Assert.IsType<OrderCaptureSettingsDto>(ok.Value);
        Assert.Equal(85m, settings.DefaultShippingCost);
    }
}
