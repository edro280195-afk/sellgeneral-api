using System.Security.Cryptography;
using System.Text;
using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EntregasApi.Services;

public sealed record SellerTrialDecision(bool Granted, string? RestrictionReason)
{
    public static SellerTrialDecision Allow() => new(true, null);
    public static SellerTrialDecision Restrict(string reason) => new(false, reason);
}

public interface ISellerTrialPolicy
{
    Task<SellerTrialDecision> EvaluateAsync(
        Account account,
        string? deviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Concede una sola prueba por identidad verificada y por instalacion. El
/// dispositivo es una segunda senal antifraude; el telefono sigue siendo la
/// identidad principal y debe estar confirmado por WhatsApp.
/// </summary>
public sealed class SellerTrialPolicy : ISellerTrialPolicy
{
    public const string DeviceHeaderName = "X-Device-Id";

    private const int MinimumDeviceIdLength = 32;
    private const int MaximumDeviceIdLength = 128;
    private const string DeviceHashIndexName =
        "IX_Accounts_SellerTrialDeviceHash";
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public SellerTrialPolicy(
        AppDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _db = db;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    public async Task<SellerTrialDecision> EvaluateAsync(
        Account account,
        string? deviceId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        account.SellerTrialEvaluatedAtUtc = now;

        if (string.IsNullOrWhiteSpace(account.Phone) || account.PhoneVerifiedAt is null)
        {
            return Restrict(account, "phone_not_verified");
        }

        if (account.SellerTrialGrantedAtUtc is not null)
        {
            return Restrict(account, "trial_already_used");
        }

        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        if (normalizedDeviceId is null)
        {
            return Restrict(account, "device_verification_required");
        }

        var deviceHash = HashDeviceId(normalizedDeviceId);
        var usedByAnotherAccount = await _db.Accounts
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.Id != account.Id &&
                    candidate.SellerTrialDeviceHash == deviceHash &&
                    candidate.SellerTrialGrantedAtUtc != null,
                cancellationToken);

        if (usedByAnotherAccount)
        {
            return Restrict(account, "trial_review_required");
        }

        account.SellerTrialGrantedAtUtc = now;
        account.SellerTrialDeviceHash = deviceHash;
        account.SellerTrialRestrictionReason = null;
        return SellerTrialDecision.Allow();
    }

    private static SellerTrialDecision Restrict(Account account, string reason)
    {
        account.SellerTrialRestrictionReason = reason;
        return SellerTrialDecision.Restrict(reason);
    }

    private static string? NormalizeDeviceId(string? value)
    {
        var normalized = value?.Trim();
        if (normalized is null ||
            normalized.Length is < MinimumDeviceIdLength or > MaximumDeviceIdLength)
        {
            return null;
        }

        return normalized.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? normalized
            : null;
    }

    private string HashDeviceId(string deviceId)
    {
        var pepper = _configuration["TrialProtection:DeviceHashPepper"]?.Trim();
        if (string.IsNullOrWhiteSpace(pepper))
        {
            pepper = _configuration["Jwt:Key"]
                ?? throw new InvalidOperationException(
                    "Configura TrialProtection:DeviceHashPepper o Jwt:Key.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
        return Convert.ToHexString(
                hmac.ComputeHash(Encoding.UTF8.GetBytes(deviceId)))
            .ToLowerInvariant();
    }

    public static bool IsConcurrentDeviceConflict(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: DeviceHashIndexName
        };
    }

    public static void ApplyConcurrentDeviceRestriction(Account account)
    {
        account.SellerTrialGrantedAtUtc = null;
        account.SellerTrialDeviceHash = null;
        account.SellerTrialRestrictionReason = "trial_review_required";

        foreach (var membership in account.Memberships)
        {
            var business = membership.Business;
            if (business is null || business.Id != 0) continue;
            business.SubscriptionStatus = SubscriptionStatus.Expired;
            business.TrialEndsAt = null;
        }
    }
}
