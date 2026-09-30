using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// El contenedor de dependencias debe entregarle al servicio de push los DOS canales de FCM
/// (app de clientas/vendedoras y choferes). Si el de choferes no se inyectara, sus avisos
/// saldrían por el proyecto de Firebase equivocado y no llegarían.
/// </summary>
public class PushServiceWiringTests
{
    [Fact]
    public async Task Container_InjectsTheDriversChannel_AndKeepsItApartFromTheAppOne()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase($"wiring-{Guid.NewGuid():N}"));

        var app = new RecordingFcmService();
        var drivers = new RecordingDriverFcmService();
        services.AddSingleton<IFcmService>(app);
        services.AddSingleton<IDriverFcmService>(drivers);
        // Igual que Program.cs.
        services.AddScoped<IPushNotificationService, PushNotificationService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Businesses.Add(new Business
        {
            Name = "Regi Bazar", Slug = "regibazar", City = "Nuevo Laredo", FrontendUrl = "https://example.com",
            BrandPrimaryColor = "#FF0072", DepotLat = 27.4861, DepotLng = -99.5069,
        });
        await db.SaveChangesAsync();
        db.FcmTokens.Add(new FcmToken { BusinessId = 1, Token = "drv-1", Role = "driver", DriverRouteToken = "route-1" });
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<IPushNotificationService>()
            .NotifyDriverFcmAsync("route-1", "🔄 Ruta reordenada", "Nuevo orden");

        Assert.Empty(app.Calls);
        Assert.Equal(new[] { "drv-1" }, drivers.AllTokens);
    }

    [Fact]
    public async Task Container_WithoutADriversChannel_FallsBackToTheAppOne()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase($"wiring-{Guid.NewGuid():N}"));
        var app = new RecordingFcmService();
        services.AddSingleton<IFcmService>(app);
        services.AddScoped<IPushNotificationService, PushNotificationService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.FcmTokens.Add(new FcmToken { BusinessId = 1, Token = "drv-1", Role = "driver", DriverRouteToken = "route-1" });
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<IPushNotificationService>()
            .NotifyDriverFcmAsync("route-1", "t", "b");

        Assert.Equal(new[] { "drv-1" }, app.AllTokens);
    }
}
