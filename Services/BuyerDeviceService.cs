using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerDeviceService
{
    /// <summary>
    /// Registra (o actualiza) el token FCM del dispositivo de la compradora.
    /// Upsert por Token: si el mismo token ya existía bajo otra Account
    /// (cambio de sesión en el dispositivo), se reasigna.
    /// </summary>
    Task RegisterAsync(int accountId, RegisterDeviceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Quita el token (logout), para no seguir empujando push a esa Account.</summary>
    Task UnregisterAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envia una notificacion de prueba SOLO a los dispositivos de esta cuenta y
    /// devuelve el diagnostico (Firebase configurado, proyecto, tokens, enviados).
    /// </summary>
    Task<DevicePushTestDto> SendTestPushAsync(int accountId, CancellationToken cancellationToken = default);
}

public class BuyerDeviceService : IBuyerDeviceService
{
    private readonly AppDbContext _db;
    private readonly IFcmDiagnostics? _fcm;

    public BuyerDeviceService(AppDbContext db, IFcmDiagnostics? fcm = null)
    {
        _db = db;
        _fcm = fcm;
    }

    public async Task<DevicePushTestDto> SendTestPushAsync(int accountId, CancellationToken cancellationToken = default)
    {
        var tokens = await _db.BuyerDeviceTokens.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .Select(t => t.Token)
            .ToListAsync(cancellationToken);

        if (_fcm is null)
        {
            return new DevicePushTestDto(false, null, tokens.Count, 0, 0, new List<string>());
        }

        var result = await _fcm.SendTestAsync(
            tokens,
            "Notificaciones activas",
            "Si ves esto, tu telefono ya recibe avisos de Neni's App.",
            new Dictionary<string, string> { ["type"] = "test" });

        return new DevicePushTestDto(
            result.FirebaseConfigured,
            result.ProjectId,
            tokens.Count,
            result.Sent,
            result.Failed,
            result.Errors.ToList());
    }

    public async Task RegisterAsync(int accountId, RegisterDeviceRequest request, CancellationToken cancellationToken = default)
    {
        var token = request.Token.Trim();
        var existing = await _db.BuyerDeviceTokens
            .FirstOrDefaultAsync(t => t.Token == token, cancellationToken);

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            _db.BuyerDeviceTokens.Add(new BuyerDeviceToken
            {
                AccountId = accountId,
                Token = token,
                Platform = string.IsNullOrWhiteSpace(request.Platform) ? "android" : request.Platform,
                CreatedAt = now,
                LastSeenAt = now,
            });
        }
        else
        {
            existing.AccountId = accountId;
            existing.Platform = string.IsNullOrWhiteSpace(request.Platform) ? existing.Platform : request.Platform;
            existing.LastSeenAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UnregisterAsync(string token, CancellationToken cancellationToken = default)
    {
        var trimmed = token.Trim();
        var existing = await _db.BuyerDeviceTokens
            .FirstOrDefaultAsync(t => t.Token == trimmed, cancellationToken);

        if (existing is null) return;

        _db.BuyerDeviceTokens.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
