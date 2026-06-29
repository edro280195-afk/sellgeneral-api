using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerAddressService
{
    /// <summary>
    /// Lista las direcciones de la compradora (un registro por Client
    /// reclamado), cross-tenant por AccountId.
    /// </summary>
    Task<List<BuyerAddressDto>> GetMyAddressesAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Actualiza la dirección del Client indicado. Lanza
    /// <see cref="AddressNotFoundException"/> si la clienta no
    /// pertenece a la Account.
    /// </summary>
    Task<BuyerAddressDto> UpdateAddressAsync(
        int accountId,
        int clientId,
        UpdateBuyerAddressRequest request,
        CancellationToken cancellationToken = default);
}

public class AddressNotFoundException : Exception
{
    public AddressNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Direcciones de la compradora (cross-tenant por AccountId). El
/// modelo actual asume 1 dirección por Client (string en `Client.Address`).
/// Si en el futuro se quiere múltiples direcciones, se agrega un
/// `ClientAddress` con FK a Client + migración.
/// </summary>
public class BuyerAddressService : IBuyerAddressService
{
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;

    public BuyerAddressService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<BuyerAddressDto>> GetMyAddressesAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var clients = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => new
            {
                c.Id,
                c.BusinessId,
                c.Address,
                c.Latitude,
                c.Longitude,
                c.DeliveryInstructions,
            })
            .ToListAsync(cancellationToken);

        if (clients.Count == 0)
        {
            return new List<BuyerAddressDto>();
        }

        var businessIds = clients.Select(c => c.BusinessId).Distinct().ToList();
        var businesses = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => businessIds.Contains(b.Id))
            .Select(b => new BizLite(b.Id, b.Name, b.BrandPrimaryColor, b.LogoUrl))
            .ToListAsync(cancellationToken);
        var bizById = businesses.ToDictionary(b => b.Id);

        return clients.Select(c =>
        {
            var biz = bizById.TryGetValue(c.BusinessId, out var b) ? b : null;
            return new BuyerAddressDto(
                ClientId: c.Id,
                BusinessId: c.BusinessId,
                BusinessName: biz?.Name ?? "",
                BrandPrimaryColor: !string.IsNullOrWhiteSpace(biz?.BrandPrimaryColor)
                    ? biz!.BrandPrimaryColor
                    : DefaultBrandColor,
                LogoUrl: biz?.LogoUrl,
                Address: c.Address,
                Latitude: c.Latitude,
                Longitude: c.Longitude,
                DeliveryInstructions: c.DeliveryInstructions);
        })
        .OrderBy(a => a.BusinessName)
        .ToList();
    }

    public async Task<BuyerAddressDto> UpdateAddressAsync(
        int accountId,
        int clientId,
        UpdateBuyerAddressRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = await _db.Clients.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client is null || client.AccountId != accountId)
        {
            throw new AddressNotFoundException("Esta dirección no está en tu cuenta.");
        }

        // Solo se modifican los campos no-null en el request.
        if (request.Address != null)
        {
            client.Address = string.IsNullOrWhiteSpace(request.Address)
                ? null
                : request.Address.Trim();
        }
        if (request.Latitude.HasValue) client.Latitude = request.Latitude;
        if (request.Longitude.HasValue) client.Longitude = request.Longitude;
        if (request.DeliveryInstructions != null)
        {
            client.DeliveryInstructions = string.IsNullOrWhiteSpace(request.DeliveryInstructions)
                ? null
                : request.DeliveryInstructions.Trim();
        }

        await _db.SaveChangesAsync(cancellationToken);

        var biz = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == client.BusinessId)
            .Select(b => new BizLite(b.Id, b.Name, b.BrandPrimaryColor, b.LogoUrl))
            .FirstOrDefaultAsync(cancellationToken);

        return new BuyerAddressDto(
            ClientId: client.Id,
            BusinessId: client.BusinessId,
            BusinessName: biz?.Name ?? "",
            BrandPrimaryColor: !string.IsNullOrWhiteSpace(biz?.BrandPrimaryColor)
                ? biz!.BrandPrimaryColor
                : DefaultBrandColor,
            LogoUrl: biz?.LogoUrl,
            Address: client.Address,
            Latitude: client.Latitude,
            Longitude: client.Longitude,
            DeliveryInstructions: client.DeliveryInstructions);
    }

    private record BizLite(int Id, string Name, string BrandPrimaryColor, string? LogoUrl);
}
