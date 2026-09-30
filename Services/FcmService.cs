using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace EntregasApi.Services;

public interface IFcmService
{
    Task SendToTokensAsync(IEnumerable<string> fcmTokens, string title, string body, Dictionary<string, string>? data = null);
    Task SendToTokenAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null);
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

    public FcmService(ILogger<FcmService> logger)
    {
        _logger = logger;
    }

    public async Task SendToTokensAsync(IEnumerable<string> fcmTokens, string title, string body, Dictionary<string, string>? data = null)
    {
        var tokenList = fcmTokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        if (tokenList.Count == 0) return;

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
                var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message);
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
            var msgId = await FirebaseMessaging.DefaultInstance.SendAsync(message);
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
        var app = FirebaseApp.DefaultInstance;
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
            var response = await FirebaseMessaging.DefaultInstance.SendEachAsync(messages);
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
