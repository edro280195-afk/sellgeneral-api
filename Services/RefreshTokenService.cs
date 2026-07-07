using System.Security.Cryptography;
using System.Text;
using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

/// <summary>Cuenta + nuevo refresh token tras una rotación exitosa.</summary>
public sealed record RefreshResult(Account Account, string RefreshToken);

public interface IRefreshTokenService
{
    /// <summary>Emite un refresh token nuevo para la cuenta y devuelve el valor crudo.</summary>
    Task<string> IssueAsync(int accountId, CancellationToken ct = default);

    /// <summary>
    /// Valida y rota el refresh token: revoca el actual y emite uno nuevo. Devuelve
    /// null si es inválido/expirado/revocado. Si detecta reuso de uno ya revocado,
    /// revoca todos los de la cuenta (posible robo) y devuelve null.
    /// </summary>
    Task<RefreshResult?> RotateAsync(string rawToken, CancellationToken ct = default);

    /// <summary>Revoca el refresh token (logout). Silencioso si no existe.</summary>
    Task RevokeAsync(string rawToken, CancellationToken ct = default);
}

public class RefreshTokenService : IRefreshTokenService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    private readonly AppDbContext _db;

    public RefreshTokenService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<string> IssueAsync(int accountId, CancellationToken ct = default)
    {
        var raw = GenerateRaw();
        _db.RefreshTokens.Add(new RefreshToken
        {
            AccountId = accountId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTime.UtcNow.Add(Lifetime),
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    public async Task<RefreshResult?> RotateAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;

        var hash = Hash(rawToken);
        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return null;

        // Reuso de un token ya revocado => posible robo: revoca toda la cuenta.
        if (token.RevokedAt is not null)
        {
            await RevokeAllForAccountAsync(token.AccountId, ct);
            return null;
        }

        if (DateTime.UtcNow >= token.ExpiresAt) return null;

        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Id == token.AccountId, ct);
        if (account is null) return null;

        var newRaw = GenerateRaw();
        token.RevokedAt = DateTime.UtcNow;
        token.ReplacedByHash = Hash(newRaw);
        _db.RefreshTokens.Add(new RefreshToken
        {
            AccountId = account.Id,
            TokenHash = token.ReplacedByHash,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime),
        });
        await _db.SaveChangesAsync(ct);

        return new RefreshResult(account, newRaw);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return;

        var hash = Hash(rawToken);
        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct);
        if (token is null) return;

        token.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task RevokeAllForAccountAsync(int accountId, CancellationToken ct)
    {
        var active = await _db.RefreshTokens
            .Where(t => t.AccountId == accountId && t.RevokedAt == null)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var t in active) t.RevokedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private static string GenerateRaw() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}
