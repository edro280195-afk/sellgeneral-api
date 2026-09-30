using EntregasApi.Services;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// Un token FCM solo lo acepta el proyecto de Firebase con el que se registró. Los avisos a
/// choferes salen por su propia FirebaseApp ("drivers") cuando existe; si no, por la principal.
/// </summary>
public class FcmAppsTests
{
    [Fact]
    public void Resolve_ReturnsTheNamedApp_WhenItExists()
    {
        var name = $"drivers-test-{Guid.NewGuid():N}";
        var app = FirebaseApp.Create(
            new AppOptions { Credential = GoogleCredential.FromAccessToken("token-de-prueba"), ProjectId = "proyecto-choferes" },
            name);
        try
        {
            Assert.Same(app, FcmApps.Resolve(name));
        }
        finally
        {
            app.Delete();
        }
    }

    [Fact]
    public void Resolve_FallsBackToTheMainApp_WhenTheNamedOneDoesNotExist()
    {
        var unknown = $"no-existe-{Guid.NewGuid():N}";

        // Sin segunda credencial, los choferes usan la misma app que las clientas y vendedoras.
        Assert.Same(FirebaseApp.DefaultInstance, FcmApps.Resolve(unknown));
        Assert.Same(FirebaseApp.DefaultInstance, FcmApps.Resolve(null));
    }

    [Fact]
    public void DriverChannel_IsSeparateFromTheAppChannel_ButKeepsTheSameShape()
    {
        // Mismo contrato (IFcmService) pero un canal distinto: no se pueden confundir al inyectarlos.
        Assert.True(typeof(IFcmService).IsAssignableFrom(typeof(IDriverFcmService)));
        Assert.False(typeof(IDriverFcmService).IsAssignableFrom(typeof(FcmService)));
        Assert.True(typeof(IDriverFcmService).IsAssignableFrom(typeof(DriverFcmService)));
        Assert.Equal("drivers", FcmApps.DriversAppName);
    }
}
