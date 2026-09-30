using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace EntregasApi.Services;

public interface IFcmService
{
    Task SendToTokensAsync(IEnumerable<string> fcmTokens, string title, string body, Dictionary<string, string>? data = null);
    Task SendToTokenAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null);
}

/// <summary>
/// FCM hacia los dispositivos de los CHOFERES. Tiene la misma forma que <see cref="IFcmService"/>
/// (el de la app de clientas y vendedoras) pero sale por el proyecto de Firebase de los choferes
/// cuando hay una segunda credencial configurada (<see cref="FcmApps.DriversAppName"/>). Un token
/// FCM solo lo acepta el proyecto con el que se registró: con una sola credencial en el servidor,
/// los dispositivos del otro proyecto fallaban con <c>SenderIdMismatch</c> y no recibían nada.
/// Sin segunda credencial usa la misma que la app (por ejemplo, cuando la app de choferes nueva
/// viva en el mismo proyecto).
/// </summary>
public interface IDriverFcmService : IFcmService
{
}

/// <summary>Resuelve qué <see cref="FirebaseApp"/> usa cada canal de FCM.</summary>
public static class FcmApps
{
    /// <summary>Nombre de la segunda FirebaseApp (choferes); se crea en Program.cs solo si hay credencial.</summary>
    public const string DriversAppName = "drivers";

    /// <summary>
    /// La app con ese nombre si existe; si no, la principal (la de clientas y vendedoras).
    /// Devuelve <c>null</c> cuando Firebase no está configurado en este servidor.
    /// </summary>
    public static FirebaseApp? Resolve(string? appName)
    {
        if (!string.IsNullOrEmpty(appName))
        {
            var named = FirebaseApp.GetInstance(appName);
            if (named is not null) return named;
        }
        return FirebaseApp.DefaultInstance;
    }
}

/// <summary>Resultado de una notificacion de prueba para diagnosticar el envio.</summary>
public sealed record FcmTestResult(
    bool FirebaseConfigured,
    string? ProjectId,
    int Sent,
    int Failed,
    IReadOnlyList<string> Errors);

/// <summary>
/// Diagnostico de envio FCM: a diferencia de <see cref="IFcmService"/>, que
/// traga los errores y solo los registra, esto los devuelve para poder
/// comprobar de punta a punta que la credencial de Firebase Admin es del
/// proyecto correcto y que el token del dispositivo es entregable.
/// </summary>
public interface IFcmDiagnostics
{
    Task<FcmTestResult> SendTestAsync(
        IEnumerable<string> fcmTokens, string title, string body,
        Dictionary<string, string>? data = null);
}

public class FcmService : IFcmService, IFcmDiagnostics
{
    private readonly ILogger<FcmService> _logger;
    private readonly string? _appName;

    /// <param name="appName">
    /// FirebaseApp a usar (<c>null</c> = la principal). Si ese nombre no existe se usa la principal.
    /// </param>
    public FcmService(ILogger<FcmService> logger, string? appName = null)
    {
        _logger = logger;
        _appName = appName;
    }

    private FirebaseMessaging? Messaging()
    {
        var app = FcmApps.Resolve(_appName);
        return app is null ? null : FirebaseMessaging.GetMessaging(app);
    }

    public async Task SendToTokensAsync(IEnumerable<string> fcmTokens, string title, string body, Dictionary<string, string>? data = null)
    {
        var tokenList = fcmTokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        if (tokenList.Count == 0) return;

        var messaging = Messaging();
        if (messaging is null)
        {
            _logger.LogWarning("FCM no está configurado en este servidor; se omite el envío a {Count} dispositivo(s).", tokenList.Count);
            return;
        }

        // FCM multicast: máx 500 tokens por llamada
        const int chunkSize = 500;
        for (int i = 0; i < tokenList.Count; i += chunkSize)
        {
            var chunk = tokenList.Skip(i).Take(chunkSize).ToList();
            var message = new MulticastMessage
            {
                Tokens = chunk,
                Notification = new Notification { Title = title, Body = body },
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        Title = title,
                        Body = body,
                        Icon = "ic_notification",
                        Color = "#FF0072",
                        ChannelId = "regibazar_channel"
                    }
                },
                Data = data
            };

            try
            {
                var response = await messaging.SendEachForMulticastAsync(message);
                _logger.LogInformation("FCM multicast: {Success}/{Total} enviados.", response.SuccessCount, chunk.Count);

                // Log tokens fallidos (expirados, inválidos)
                for (int j = 0; j < response.Responses.Count; j++)
                {
                    if (!response.Responses[j].IsSuccess)
                    {
                        _logger.LogWarning("FCM token fallido [{Token}]: {Error}", chunk[j], response.Responses[j].Exception?.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando FCM multicast.");
            }
        }
    }

    public async Task SendToTokenAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null)
    {
        if (string.IsNullOrWhiteSpace(fcmToken)) return;

        var messaging = Messaging();
        if (messaging is null)
        {
            _logger.LogWarning("FCM no está configurado en este servidor; se omite el envío a un dispositivo.");
            return;
        }

        var message = new Message
        {
            Token = fcmToken,
            Notification = new Notification { Title = title, Body = body },
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    Title = title,
                    Body = body,
                    Icon = "ic_notification",
                    Color = "#FF0072",
                    ChannelId = "regibazar_channel"
                }
            },
            Data = data
        };

        try
        {
            var msgId = await messaging.SendAsync(message);
            _logger.LogInformation("FCM enviado: {MsgId}", msgId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando FCM a token {Token}.", fcmToken);
        }
    }

    public async Task<FcmTestResult> SendTestAsync(
        IEnumerable<string> fcmTokens, string title, string body,
        Dictionary<string, string>? data = null)
    {
        var app = FcmApps.Resolve(_appName);
        if (app is null)
        {
            return new FcmTestResult(false, null, 0, 0, Array.Empty<string>());
        }

        string? projectId = null;
        try
        {
            projectId = (app.Options.Credential?.UnderlyingCredential as ServiceAccountCredential)?.ProjectId
                        ?? app.Options.ProjectId;
        }
        catch
        {
            // El proyecto es solo informativo para el diagnostico.
        }

        var tokens = fcmTokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(20).ToList();
        if (tokens.Count == 0)
        {
            return new FcmTestResult(true, projectId, 0, 0, Array.Empty<string>());
        }

        var messages = tokens.Select(token => new Message
        {
            Token = token,
            Notification = new Notification { Title = title, Body = body },
            Android = new AndroidConfig { Priority = Priority.High },
            Data = data,
        }).ToList();

        var errors = new List<string>();
        var sent = 0;
        try
        {
            var response = await FirebaseMessaging.GetMessaging(app).SendEachAsync(messages);
            sent = response.SuccessCount;
            foreach (var item in response.Responses.Where(r => !r.IsSuccess))
            {
                errors.Add(item.Exception?.MessagingErrorCode?.ToString()
                           ?? item.Exception?.Message
                           ?? "Error desconocido");
            }
            _logger.LogInformation("FCM prueba: {Success}/{Total} enviados (proyecto {Project}).",
                response.SuccessCount, tokens.Count, projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando FCM de prueba.");
            errors.Add(ex.Message);
        }

        return new FcmTestResult(true, projectId, sent, tokens.Count - sent, errors);
    }
}

/// <summary>FCM de los choferes: el mismo envío, por el proyecto de Firebase de los choferes.</summary>
public sealed class DriverFcmService : FcmService, IDriverFcmService
{
    public DriverFcmService(ILogger<FcmService> logger) : base(logger, FcmApps.DriversAppName)
    {
    }
}
