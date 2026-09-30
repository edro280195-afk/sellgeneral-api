using System.Text.RegularExpressions;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// Candados de arquitectura para que los avisos no se mezclen: todo lo que decide A
/// QUIÉN llega una notificación vive en <c>PushNotificationService</c>. Si alguien
/// crea una <c>Notification</c> a mano en un controlador (sin destinatario), o manda FCM
/// directo a tokens, o lee los dispositivos de las cuentas desde otro lado, estas
/// pruebas fallan y obligan a pasar por el servicio (y por sus pruebas de destinatarios).
/// </summary>
public class NotificationArchitectureTests
{
    // Carpetas de código de producción que se revisan.
    private static readonly string[] ProductionFolders = { "Controllers", "Services", "Hubs", "Models", "Data" };

    [Fact]
    public void OnlyPushNotificationService_CreatesNotificationRows()
    {
        var offenders = FilesMatching(
            new Regex(@"\bNotifications\s*\.\s*(Add|AddRange|AddAsync)\s*\(", RegexOptions.Compiled),
            allowed: "Services/PushNotificationService.cs");

        Assert.True(offenders.Count == 0,
            "Las notificaciones solo se crean en PushNotificationService (ahí se fija el destinatario). " +
            $"Se creaban en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyPushNotificationService_SendsFcmToDevices()
    {
        var offenders = FilesMatching(
            new Regex(@"\.\s*SendTo(Tokens|Token)Async\s*\(", RegexOptions.Compiled),
            allowed: "Services/PushNotificationService.cs");

        Assert.True(offenders.Count == 0,
            "El envío FCM a dispositivos solo sale de PushNotificationService. " +
            $"Se enviaba directo en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void DeviceTokens_AreOnlyReadByTheServicesThatOwnThem()
    {
        var offenders = FilesMatching(
            new Regex(@"\bBuyerDeviceTokens\b", RegexOptions.Compiled),
            allowed: new[]
            {
                "Services/PushNotificationService.cs", // resuelve destinatarias
                "Services/BuyerDeviceService.cs",      // registro / baja / prueba
                "Controllers/AuthController.cs",       // borrado de cuenta
                "Data/AppDbContext.cs",
            });

        Assert.True(offenders.Count == 0,
            "Los dispositivos de las cuentas solo los leen el servicio de push y el de dispositivos. " +
            $"Se usaban en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void DriverDevices_AreOnlyReadByThePushService_AndTheirRegistrationEndpoint()
    {
        var offenders = FilesMatching(
            new Regex(@"\bFcmTokens\b", RegexOptions.Compiled),
            allowed: new[]
            {
                "Services/PushNotificationService.cs",
                "Controllers/PushController.cs",
                "Data/AppDbContext.cs",
                "Models/FcmToken.cs", // el nombre de la tabla
            });

        Assert.True(offenders.Count == 0,
            "Los dispositivos de choferes solo los leen el servicio de push y su endpoint de registro. " +
            $"Se usaban en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoCodeCallsTheRemovedAdminBroadcasts()
    {
        // SendNotificationToAdminsAsync avisaba por Web Push a "los admins del negocio activo",
        // un canal que la app nativa nunca llena y que dependía del tenant ambiental (que sin
        // token cae al negocio #1). BroadcastToAllDriversAsync avisaba a TODOS los choferes.
        var offenders = FilesMatching(
            new Regex(@"\b(SendNotificationToAdminsAsync|BroadcastToAllDriversAsync)\b", RegexOptions.Compiled),
            allowed: Array.Empty<string>());

        Assert.True(offenders.Count == 0,
            "Esos envíos ya no existen: usa SendNotificationToBusinessOwnersAsync (vendedoras) o " +
            $"NotifyDriverFcmAsync (chofer de una ruta). Quedaban en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void DriverEnRouteAlert_IsOnlySentWhenHerDeliveryBecomesTheCurrentOne()
    {
        // "Va en camino" le llega a cada clienta cuando de verdad es la siguiente de la
        // lista: al iniciar la ruta, al avanzar a la siguiente parada, al reordenar o al
        // marcar en tránsito (todo en DriverController). Jamás a todas a la vez al crear o
        // modificar la ruta (RoutesController).
        var offenders = FilesMatching(
            new Regex(@"\bNotifyClientDriverEnRouteAsync\s*\(", RegexOptions.Compiled),
            allowed: new[]
            {
                "Services/PushNotificationService.cs", // la definición
                "Controllers/DriverController.cs",
            });

        Assert.True(offenders.Count == 0,
            "El aviso 'va en camino' solo se manda desde DriverController, cuando a la clienta le toca. " +
            $"Se mandaba también en: {string.Join(", ", offenders)}");
    }

    // ═══════════════════════════════════════════

    private static List<string> FilesMatching(Regex pattern, params string[] allowed)
    {
        var root = FindApiRoot();
        var allowedSet = allowed.Select(a => a.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();

        foreach (var folder in ProductionFolders)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (allowedSet.Contains(relative)) continue;
                if (pattern.IsMatch(StripComments(File.ReadAllText(file)))) offenders.Add(relative);
            }
        }

        return offenders;
    }

    /// <summary>Quita comentarios para que una mención en un comentario no cuente como uso.</summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(source, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string FindApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EntregasApi.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("No se encontró la raíz del proyecto EntregasApi desde " + AppContext.BaseDirectory);
    }
}
