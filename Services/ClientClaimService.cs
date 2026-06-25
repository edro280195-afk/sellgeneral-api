using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public class ClientClaimService : IClientClaimService
{
    private readonly AppDbContext _db;

    public ClientClaimService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ClaimOutcome> ClaimByOrderTokenAsync(
        int accountId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new ClaimOutcome(ClaimStatus.NoProof, null, "Falta el token del pedido.");
        }

        // 1) Account debe existir y tener un método de identidad (defensa en profundidad:
        //    el CHECK constraint ya lo garantiza, pero validamos por si la fila viene de
        //    una migración manual o un test con datos rotos).
        var account = await _db.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

        if (account is null)
        {
            return new ClaimOutcome(ClaimStatus.Forbidden, null, "La cuenta no existe.");
        }

        if (string.IsNullOrWhiteSpace(account.Phone) &&
            string.IsNullOrWhiteSpace(account.FacebookUserId) &&
            string.IsNullOrWhiteSpace(account.Email))
        {
            return new ClaimOutcome(
                ClaimStatus.Forbidden,
                null,
                "La cuenta no tiene ningún método de identidad vinculado.");
        }

        // 2) Buscar el Order por AccessToken. cross-tenant (IgnoreQueryFilters) porque el
        //    pedido puede ser de cualquier negocio. La posesión del token es la prueba.
        var orderInfo = await _db.Orders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.AccessToken == accessToken)
            .Select(o => new { o.Id, o.ClientId, o.BusinessId })
            .FirstOrDefaultAsync(cancellationToken);

        if (orderInfo is null)
        {
            return new ClaimOutcome(ClaimStatus.NotFound, null, "El pedido no existe.");
        }

        // 3) Cargar el Client dueño del pedido.
        var client = await _db.Clients
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == orderInfo.ClientId, cancellationToken);

        if (client is null)
        {
            return new ClaimOutcome(ClaimStatus.NotFound, null, "La clienta del pedido no existe.");
        }

        // 4) Si ya está enlazado a otra Account: ROBOCONFIRMA la propiedad. NO lo
        //    movemos a esta Account; devolvemos AlreadyClaimedByOther para que la UI
        //    muestre el error sin filtrar a quién pertenece.
        if (client.AccountId.HasValue && client.AccountId.Value != accountId)
        {
            return new ClaimOutcome(
                ClaimStatus.AlreadyClaimedByOther,
                null,
                "Este perfil ya fue reclamado por otra cuenta.");
        }

        var now = DateTime.UtcNow;

        // 5) Si ya está enlazado a ESTA Account: idempotente.
        if (client.AccountId.HasValue && client.AccountId.Value == accountId)
        {
            var existingAudit = await _db.ClientClaimAudits
                .AsNoTracking()
                .Where(a => a.AccountId == accountId && a.ClientId == client.Id)
                .OrderByDescending(a => a.ClaimedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var businessName = await _db.Businesses
                .AsNoTracking()
                .Where(b => b.Id == client.BusinessId)
                .Select(b => b.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? "";

            return new ClaimOutcome(
                ClaimStatus.Linked,
                new ClientClaimResultDto(
                    client.Id,
                    client.BusinessId,
                    businessName,
                    client.Name,
                    "idempotent",
                    existingAudit?.ClaimedAt ?? now),
                "El perfil ya estaba vinculado a esta cuenta.");
        }

        // 6) Enlazar: Client.AccountId = accountId.
        client.AccountId = accountId;
        _db.Clients.Update(client);

        _db.ClientClaimAudits.Add(new ClientClaimAudit
        {
            AccountId = accountId,
            ClientId = client.Id,
            BusinessId = client.BusinessId,
            Mode = ClientClaimMode.OrderToken,
            Reason = $"order token Order#{orderInfo.Id}",
            ClaimedAt = now,
        });

        await _db.SaveChangesAsync(cancellationToken);

        var claimedBusinessName = await _db.Businesses
            .AsNoTracking()
            .Where(b => b.Id == client.BusinessId)
            .Select(b => b.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "";

        return new ClaimOutcome(
            ClaimStatus.Linked,
            new ClientClaimResultDto(
                client.Id,
                client.BusinessId,
                claimedBusinessName,
                client.Name,
                "order-token",
                now),
            null);
    }

    public async Task<List<ClientClaimCandidateDto>> FindClaimCandidatesByPhoneAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var account = await _db.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

        if (account is null || string.IsNullOrWhiteSpace(account.NormalizedPhoneIfAny()))
        {
            return new List<ClientClaimCandidateDto>();
        }

        var normalizedPhone = account.NormalizedPhoneIfAny()!;

        // Cross-tenant: los Client pueden estar en cualquier Business. Pero NO
        // exponemos teléfono, dirección ni pedidos: solo el nombre del Client +
        // nombre del negocio + conteo público de pedidos.
        var candidates = await _db.Clients
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(c => c.NormalizedPhone == normalizedPhone)
            .Where(c => c.AccountId == null || c.AccountId == accountId)
            .GroupJoin(
                _db.Businesses.AsNoTracking(),
                c => c.BusinessId,
                b => b.Id,
                (c, bs) => new { c, bs })
            .SelectMany(
                x => x.bs.DefaultIfEmpty(),
                (x, b) => new
                {
                    x.c.Id,
                    x.c.BusinessId,
                    BusinessName = b != null ? b.Name : "",
                    BusinessCity = b != null ? b.City : null,
                    x.c.Name,
                    OrdersCount = x.c.Orders.Count(),
                    LastOrderAt = x.c.Orders.Max(o => (DateTime?)o.CreatedAt),
                })
            .OrderByDescending(x => x.OrdersCount)
            .ToListAsync(cancellationToken);

        return candidates
            .Select(c => new ClientClaimCandidateDto(
                ClientId: c.Id,
                BusinessId: c.BusinessId,
                BusinessName: c.BusinessName,
                ClientName: c.Name,
                City: c.BusinessCity,
                OrdersCount: c.OrdersCount,
                LastOrderAt: c.LastOrderAt,
                MatchedBy: "phone"))
            .ToList();
    }

    public async Task<ClaimOutcome> ClaimByPhoneMatchAsync(
        int accountId,
        int clientId,
        CancellationToken cancellationToken = default)
    {
        var account = await _db.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

        if (account is null)
        {
            return new ClaimOutcome(ClaimStatus.Forbidden, null, "La cuenta no existe.");
        }

        var accountPhone = account.NormalizedPhoneIfAny();
        if (string.IsNullOrWhiteSpace(accountPhone))
        {
            return new ClaimOutcome(
                ClaimStatus.Forbidden,
                null,
                "Para reclamar por teléfono la cuenta debe tener un teléfono verificado.");
        }

        // Cross-tenant: el Client puede estar en cualquier Business.
        var client = await _db.Clients
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client is null)
        {
            return new ClaimOutcome(ClaimStatus.NotFound, null, "La clienta no existe.");
        }

        // PRUEBA: Account.NormalizedPhone == Client.NormalizedPhone. Sin match, NO hay enlace.
        if (string.IsNullOrWhiteSpace(client.NormalizedPhone) ||
            client.NormalizedPhone != accountPhone)
        {
            return new ClaimOutcome(
                ClaimStatus.NoProof,
                null,
                "Esta clienta no coincide con tu teléfono.");
        }

        if (client.AccountId.HasValue && client.AccountId.Value != accountId)
        {
            return new ClaimOutcome(
                ClaimStatus.AlreadyClaimedByOther,
                null,
                "Este perfil ya fue reclamado por otra cuenta.");
        }

        var now = DateTime.UtcNow;

        if (client.AccountId.HasValue && client.AccountId.Value == accountId)
        {
            var existingAudit = await _db.ClientClaimAudits
                .AsNoTracking()
                .Where(a => a.AccountId == accountId && a.ClientId == client.Id)
                .OrderByDescending(a => a.ClaimedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var businessName = await _db.Businesses
                .AsNoTracking()
                .Where(b => b.Id == client.BusinessId)
                .Select(b => b.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? "";

            return new ClaimOutcome(
                ClaimStatus.Linked,
                new ClientClaimResultDto(
                    client.Id,
                    client.BusinessId,
                    businessName,
                    client.Name,
                    "idempotent",
                    existingAudit?.ClaimedAt ?? now),
                "El perfil ya estaba vinculado a esta cuenta.");
        }

        client.AccountId = accountId;
        _db.Clients.Update(client);

        _db.ClientClaimAudits.Add(new ClientClaimAudit
        {
            AccountId = accountId,
            ClientId = client.Id,
            BusinessId = client.BusinessId,
            Mode = ClientClaimMode.PhoneMatch,
            Reason = $"phone match ({accountPhone})",
            ClaimedAt = now,
        });

        await _db.SaveChangesAsync(cancellationToken);

        var claimedBusinessName = await _db.Businesses
            .AsNoTracking()
            .Where(b => b.Id == client.BusinessId)
            .Select(b => b.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "";

        return new ClaimOutcome(
            ClaimStatus.Linked,
            new ClientClaimResultDto(
                client.Id,
                client.BusinessId,
                claimedBusinessName,
                client.Name,
                "phone-match",
                now),
            null);
    }

    public async Task<List<ClaimedClientSummaryDto>> ListClaimedClientsAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        // Cross-tenant (IgnoreQueryFilters). NO exponemos teléfono, dirección ni
        // pedidos: solo identidad + nombre del negocio.
        var claimed = await _db.Clients
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .GroupJoin(
                _db.ClientClaimAudits.AsNoTracking().Where(a => a.AccountId == accountId),
                c => c.Id,
                a => a.ClientId,
                (c, audits) => new { c, audits })
            .SelectMany(
                x => x.audits.DefaultIfEmpty(),
                (x, a) => new
                {
                    x.c.Id,
                    x.c.BusinessId,
                    x.c.Name,
                    LinkedBy = a != null ? a.Mode.ToString() : "Unknown",
                    ClaimedAt = a != null ? (DateTime?)a.ClaimedAt : null,
                })
            .ToListAsync(cancellationToken);

        var businessIds = claimed.Select(x => x.BusinessId).Distinct().ToList();
        var businessNames = await _db.Businesses
            .AsNoTracking()
            .Where(b => businessIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        return claimed
            .GroupBy(x => x.Id)
            .Select(g =>
            {
                var first = g.OrderByDescending(x => x.ClaimedAt ?? DateTime.MinValue).First();
                return new ClaimedClientSummaryDto(
                    ClientId: first.Id,
                    BusinessId: first.BusinessId,
                    BusinessName: businessNames.TryGetValue(first.BusinessId, out var n) ? n : "",
                    ClientName: first.Name,
                    LinkedBy: first.LinkedBy,
                    ClaimedAt: first.ClaimedAt ?? DateTime.UtcNow);
            })
            .OrderBy(c => c.BusinessName)
            .ThenBy(c => c.ClientName)
            .ToList();
    }
}

internal static class AccountClaimExtensions
{
    /// <summary>
    /// Devuelve el teléfono normalizado del Account (o null si no tiene).
    /// Se calcula una sola vez para mantener los queries limpios.
    /// </summary>
    public static string? NormalizedPhoneIfAny(this Account account)
    {
        var raw = account.Phone;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return TextNormalizer.NormalizePhone(raw);
    }
}
