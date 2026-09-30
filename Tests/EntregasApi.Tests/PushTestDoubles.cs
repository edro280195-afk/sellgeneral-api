using EntregasApi.Services;

namespace EntregasApi.Tests;

/// <summary>
/// FCM falso que recuerda cada envío: a qué dispositivos se mandó y con qué
/// payload. Sirve para comprobar quién recibe (y quién NO) cada notificación.
/// </summary>
internal class RecordingFcmService : IFcmService
{
    public sealed record Sent(
        IReadOnlyList<string> Tokens,
        string Title,
        string Body,
        IReadOnlyDictionary<string, string>? Data);

    public List<Sent> Calls { get; } = new();

    /// <summary>Todos los dispositivos a los que se empujó algo, sin repetir.</summary>
    public IReadOnlyCollection<string> AllTokens =>
        Calls.SelectMany(c => c.Tokens).Distinct().ToList();

    public Task SendToTokensAsync(IEnumerable<string> fcmTokens, string title, string body, Dictionary<string, string>? data = null)
    {
        Calls.Add(new Sent(fcmTokens.ToList(), title, body, data));
        return Task.CompletedTask;
    }

    public Task SendToTokenAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null)
    {
        Calls.Add(new Sent(new[] { fcmToken }, title, body, data));
        return Task.CompletedTask;
    }
}

/// <summary>FCM falso del proyecto de Firebase de los CHOFERES (separado del de la app).</summary>
internal sealed class RecordingDriverFcmService : RecordingFcmService, IDriverFcmService
{
}

/// <summary>
/// Espía de <see cref="IPushNotificationService"/>: recuerda a qué tipo de
/// destinatario se dirigió cada aviso (y a qué negocio/clienta), para comprobar
/// que cada evento del sistema avisa a quien debe.
/// </summary>
internal sealed class RecordingPushNotificationService : IPushNotificationService
{
    public sealed record Call(
        string Audience,
        int? BusinessId,
        int? ClientId,
        string Title,
        string Message,
        string? Url,
        string? Tag);

    public const string Client = "client";
    public const string Owners = "owners";
    public const string Followers = "followers";
    public const string Driver = "driver";

    public List<Call> Calls { get; } = new();

    /// <summary>Simula que el aviso a la vendedora falla (Firebase o la base caídos).</summary>
    public bool FailOnOwners { get; set; }

    public IEnumerable<Call> To(string audience) => Calls.Where(c => c.Audience == audience);

    public Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null, string? tag = null)
    {
        Calls.Add(new Call(Client, null, clientId, title, message, url, tag));
        return Task.CompletedTask;
    }

    public Task SendNotificationToDriverAsync(string routeToken, string title, string message, string? url = null, string? tag = null)
    {
        Calls.Add(new Call(Driver, null, null, title, message, url, tag));
        return Task.CompletedTask;
    }

    public Task SendNotificationToBusinessOwnersAsync(int businessId, string title, string message, string? url = null, string? tag = null)
    {
        if (FailOnOwners) throw new InvalidOperationException("Push caído (simulado).");
        Calls.Add(new Call(Owners, businessId, null, title, message, url, tag));
        return Task.CompletedTask;
    }

    public Task SendNotificationToFollowersAsync(int businessId, string title, string message, string? url = null, string? tag = null, bool vipOnly = false, bool requireNotifyOnPost = false, bool requireNotifyOnLive = false)
    {
        Calls.Add(new Call(Followers, businessId, null, title, message, url, tag));
        return Task.CompletedTask;
    }

    public Task NotifyClientDriverEnRouteAsync(int clientId, string? driverName = null)
    {
        Calls.Add(new Call(Client, null, clientId, "en-route", "", null, "driver-en-route"));
        return Task.CompletedTask;
    }

    public Task NotifyClientDriverNearbyAsync(int clientId, int distanceMeters)
    {
        Calls.Add(new Call(Client, null, clientId, "nearby", "", null, "driver-nearby"));
        return Task.CompletedTask;
    }

    public Task NotifyClientDeliveredAsync(int clientId)
    {
        Calls.Add(new Call(Client, null, clientId, "delivered", "", null, "delivered"));
        return Task.CompletedTask;
    }

    public Task NotifyDriversNewRouteAsync(string routeName, string driverToken, int deliveryCount)
    {
        Calls.Add(new Call(Driver, null, null, routeName, "", null, "new_route"));
        return Task.CompletedTask;
    }

    public Task NotifyDriverFcmAsync(string driverRouteToken, string title, string body, Dictionary<string, string>? data = null)
    {
        Calls.Add(new Call(Driver, null, null, title, body, null, "driver-fcm"));
        return Task.CompletedTask;
    }
}

/// <summary>Cuenta autenticada (o anónima si <c>accountId</c> es null) para pruebas de controladores.</summary>
internal sealed class StubCurrentAccount(int? accountId) : ICurrentAccount
{
    public int? AccountId => accountId;
    public bool IsAuthenticated => accountId is not null;
}

/// <summary>Hub de SignalR que no hace nada: los controladores lo usan para avisos en tiempo real.</summary>
internal sealed class FakeHubContext : Microsoft.AspNetCore.SignalR.IHubContext<EntregasApi.Hubs.DeliveryHub>
{
    public Microsoft.AspNetCore.SignalR.IHubClients Clients { get; } = new FakeHubClients();

    public Microsoft.AspNetCore.SignalR.IGroupManager Groups { get; } = new FakeGroupManager();

    private sealed class FakeGroupManager : Microsoft.AspNetCore.SignalR.IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeHubClients : Microsoft.AspNetCore.SignalR.IHubClients
    {
        private static readonly Microsoft.AspNetCore.SignalR.IClientProxy Proxy = new FakeClientProxy();

        public Microsoft.AspNetCore.SignalR.IClientProxy All => Proxy;
        public Microsoft.AspNetCore.SignalR.IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public Microsoft.AspNetCore.SignalR.IClientProxy Client(string connectionId) => Proxy;
        public Microsoft.AspNetCore.SignalR.IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public Microsoft.AspNetCore.SignalR.IClientProxy Group(string groupName) => Proxy;
        public Microsoft.AspNetCore.SignalR.IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public Microsoft.AspNetCore.SignalR.IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public Microsoft.AspNetCore.SignalR.IClientProxy User(string userId) => Proxy;
        public Microsoft.AspNetCore.SignalR.IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class FakeClientProxy : Microsoft.AspNetCore.SignalR.IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>Negocio activo fijo para el DbContext y los controladores de las pruebas.</summary>
internal sealed class StubTenant(int businessId) : ICurrentTenant
{
    public int ActiveBusinessId { get; private set; } = businessId;
    public bool IsResolved => true;
    public void SetBusiness(int id) => ActiveBusinessId = id;
}
