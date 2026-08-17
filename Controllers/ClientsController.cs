using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

public record UpdateClientRequest(
    string Name,
    string? Phone,
    string? Address,
    string Tag,
    string Type,
    string? DeliveryInstructions = null,
    double? Latitude = null,
    double? Longitude = null,
    bool ClearCoordinates = false
);

public record CreateClientRequest(
    string Name,
    string? Phone = null,
    string? Address = null,
    string? Tag = null,
    string? Type = null,
    string? DeliveryInstructions = null
);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClientsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IOrderService _orderService;
    private readonly IGeocodingService _geocoding;
    private readonly IClientResolverService _resolver;

    public ClientsController(AppDbContext db, IOrderService orderService, IGeocodingService geocoding, IClientResolverService resolver)
    {
        _db = db;
        _orderService = orderService;
        _geocoding = geocoding;
        _resolver = resolver;
    }

    /// <summary>
    /// POST /api/clients - Crea una clienta directamente sin requerir un pedido ficticio.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ClientDto>> Create([FromBody] CreateClientRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "El nombre de la clienta es obligatorio." });

        var nameTrim = req.Name.Trim();
        var normalizedName = TextNormalizer.NormalizeName(nameTrim);

        var existing = await _db.Clients
            .Include(c => c.Orders)
            .Include(c => c.Aliases)
            .FirstOrDefaultAsync(c => c.NormalizedName == normalizedName);

        if (existing != null)
        {
            return Ok(new ClientDto(
                existing.Id,
                existing.Name,
                existing.Phone,
                existing.Address,
                existing.Tag.ToString(),
                existing.Orders.Count(),
                existing.Orders.Where(o => o.Status != Models.OrderStatus.Canceled).Sum(o => o.Total),
                existing.Type,
                existing.DeliveryInstructions,
                existing.Latitude,
                existing.Longitude,
                existing.Aliases.Select(a => a.Alias).ToList()
            ));
        }

        var client = new Client
        {
            Name = nameTrim,
            NormalizedName = normalizedName,
            Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim(),
            NormalizedPhone = string.IsNullOrWhiteSpace(req.Phone) ? null : TextNormalizer.NormalizePhone(req.Phone),
            Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim(),
            NormalizedAddress = string.IsNullOrWhiteSpace(req.Address) ? null : TextNormalizer.NormalizeAddress(req.Address),
            Type = string.IsNullOrWhiteSpace(req.Type) ? "Nueva" : req.Type.Trim(),
            DeliveryInstructions = string.IsNullOrWhiteSpace(req.DeliveryInstructions) ? null : req.DeliveryInstructions.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(req.Tag) && Enum.TryParse<ClientTag>(req.Tag, true, out var tag))
        {
            client.Tag = tag;
        }

        _db.Clients.Add(client);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = client.Id }, new ClientDto(
            client.Id,
            client.Name,
            client.Phone,
            client.Address,
            client.Tag.ToString(),
            0,
            0,
            client.Type,
            client.DeliveryInstructions,
            client.Latitude,
            client.Longitude,
            new List<string>()));
    }

    /// <summary>
    /// POST /api/clients/bulk-geocode - Resuelve lat/lng para los clientes recibidos cuando tienen
    /// dirección pero faltan coordenadas. Persiste el resultado en BD. Devuelve detalle por cliente.
    /// </summary>

    [HttpPost("bulk-geocode")]
    public async Task<ActionResult<List<BulkGeocodeResultDto>>> BulkGeocode([FromBody] BulkGeocodeRequest req)
    {
        if (req.ClientIds == null || req.ClientIds.Count == 0)
            return Ok(new List<BulkGeocodeResultDto>());

        var ids = req.ClientIds.Distinct().ToList();
        var clients = await _db.Clients.Where(c => ids.Contains(c.Id)).ToListAsync();

        var results = new List<BulkGeocodeResultDto>();
        foreach (var c in clients)
        {
            if (c.Latitude.HasValue && c.Longitude.HasValue)
            {
                results.Add(new BulkGeocodeResultDto(c.Id, true, c.Latitude, c.Longitude, c.Address, null));
                continue;
            }
            if (string.IsNullOrWhiteSpace(c.Address))
            {
                results.Add(new BulkGeocodeResultDto(c.Id, false, null, null, null, "Sin dirección"));
                continue;
            }

            var r = await _geocoding.GeocodeAsync(c.Address);
            if (r.Success && r.Latitude.HasValue && r.Longitude.HasValue)
            {
                c.Latitude = r.Latitude;
                c.Longitude = r.Longitude;
                results.Add(new BulkGeocodeResultDto(c.Id, true, r.Latitude, r.Longitude, r.FormattedAddress, null));
            }
            else
            {
                results.Add(new BulkGeocodeResultDto(c.Id, false, null, null, null, r.Error ?? r.Status));
            }
        }

        await _db.SaveChangesAsync();
        return Ok(results);
    }

    [HttpGet("address-suggestions")]
    public async Task<ActionResult<IReadOnlyList<AddressSuggestion>>> AddressSuggestions(
        [FromQuery] string? input,
        [FromQuery] string? sessionToken,
        CancellationToken cancellationToken)
    {
        var suggestions = await _geocoding.AutocompleteAsync(
            input ?? string.Empty,
            sessionToken,
            cancellationToken);
        return Ok(suggestions);
    }

    [HttpGet("address-details")]
    public async Task<ActionResult<AddressDetails>> AddressDetails(
        [FromQuery] string? placeId,
        [FromQuery] string? sessionToken,
        CancellationToken cancellationToken)
    {
        var details = await _geocoding.GetPlaceDetailsAsync(
            placeId ?? string.Empty,
            sessionToken,
            cancellationToken);
        return details == null ? NotFound() : Ok(details);
    }

    /// <summary>
    /// POST /api/clients/{id}/set-coordinates - Guarda lat/lng explícitas (uso del map picker).
    /// </summary>
    [HttpPost("{id:int}/set-coordinates")]
    public async Task<IActionResult> SetCoordinates(int id, [FromBody] SetClientCoordinatesRequest req)
    {
        var c = await _db.Clients.FindAsync(id);
        if (c == null) return NotFound();
        c.Latitude = req.Latitude;
        c.Longitude = req.Longitude;
        var normalizedAddress = ClientDataPolicy.NormalizeOptionalAddress(req.Address);
        if (normalizedAddress != null)
        {
            c.Address = normalizedAddress;
            c.NormalizedAddress = TextNormalizer.NormalizeAddress(normalizedAddress);
        }
        if (req.DeliveryInstructions != null)
        {
            c.DeliveryInstructions = string.IsNullOrWhiteSpace(req.DeliveryInstructions)
                ? null
                : req.DeliveryInstructions.Trim();
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    public async Task<ActionResult<List<ClientDto>>> GetAll()
    {
        var dbData = await _db.Clients
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Phone,
                c.Address,
                c.Tag,
                OrdersCount = c.Orders.Count(),
                TotalSpent = c.Orders
                    .Where(o => o.Status != Models.OrderStatus.Canceled)
                    .Sum(o => o.Total),
                    c.Type,
                    c.DeliveryInstructions,
                    c.Latitude,
                    c.Longitude,
                    Aliases = c.Aliases
                        .OrderByDescending(a => a.TimesSeen)
                        .ThenBy(a => a.Alias)
                        .Select(a => a.Alias)
                        .ToList(),
            })
            .OrderByDescending(x => x.TotalSpent)
            .ToListAsync();

        var clients = dbData.Select(c => new ClientDto(
            c.Id,
            c.Name,
            c.Phone,
            c.Address,
            c.Tag.ToString(),
            c.OrdersCount,
            c.TotalSpent,
            c.Type,
            c.DeliveryInstructions,
            Latitude: c.Latitude,
            Longitude: c.Longitude,
            Aliases: c.Aliases
        )).ToList();

        return Ok(clients);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ClientDto>> GetById(int id)
    {
        var c = await _db.Clients
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Phone,
                c.Address,
                c.Tag,
                OrdersCount = c.Orders.Count(),
                TotalSpent = c.Orders
                    .Where(o => o.Status != Models.OrderStatus.Canceled)
                    .Sum(o => o.Total),
                c.Type,
                c.DeliveryInstructions,
                c.Latitude,
                c.Longitude,
                Aliases = c.Aliases
                    .OrderByDescending(a => a.TimesSeen)
                    .ThenBy(a => a.Alias)
                    .Select(a => a.Alias)
                    .ToList(),
            })
            .FirstOrDefaultAsync(c => c.Id == id);

        if (c == null) return NotFound();

        return Ok(new ClientDto(
            c.Id,
            c.Name,
            c.Phone,
            c.Address,
            c.Tag.ToString(),
            c.OrdersCount,
            c.TotalSpent,
            c.Type,
            c.DeliveryInstructions,
            Latitude: c.Latitude,
            Longitude: c.Longitude,
            Aliases: c.Aliases));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateClientRequest req)
    {
        var client = await _db.Clients.FindAsync(id);
        if (client == null) return NotFound();

        // 1. Detectamos si hubo un cambio de categoría antes de actualizar
        bool typeChanged = client.Type != req.Type;

        // 2. Actualizamos los datos de la clienta
        // Los campos opcionales se ignoran si llegan vacíos/null: este endpoint se usa
        // desde formularios parciales (guardar solo dirección, solo tag, etc.) y antes
        // borraba los datos previamente capturados.
        client.Name = req.Name;
        client.NormalizedName = TextNormalizer.NormalizeName(req.Name);
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            client.Phone = req.Phone;
            client.NormalizedPhone = TextNormalizer.NormalizePhone(req.Phone);
        }
        if (!string.IsNullOrWhiteSpace(req.Address))
        {
            client.Address = req.Address.Trim();
            client.NormalizedAddress = TextNormalizer.NormalizeAddress(client.Address);
        }
        if (req.Latitude.HasValue && req.Longitude.HasValue)
        {
            client.Latitude = req.Latitude;
            client.Longitude = req.Longitude;
        }
        else if (req.ClearCoordinates)
        {
            client.Latitude = null;
            client.Longitude = null;
        }
        client.Type = req.Type;
        if (!string.IsNullOrWhiteSpace(req.DeliveryInstructions)) client.DeliveryInstructions = req.DeliveryInstructions;

        if (Enum.TryParse<ClientTag>(req.Tag, true, out var newTag))
        {
            client.Tag = newTag;
        }

        // 3. 🚀 MAGIA: Si el tipo cambió, recalculamos las caducidades pendientes
        if (typeChanged)
        {
            await _orderService.SyncOrderExpirationsAsync(id);
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var client = await _db.Clients.FindAsync(id);

        if (client == null) return NotFound();

        _db.Clients.Remove(client);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return BadRequest("No se puede eliminar el cliente porque tiene pedidos asociados. Borra los pedidos primero.");
        }
        return NoContent();
    }

    [HttpDelete("wipe")]
    public async Task<IActionResult> WipeAllClients()
    {
        await _db.OrderItems.ExecuteDeleteAsync();
        await _db.Deliveries.ExecuteDeleteAsync();
        await _db.Orders.ExecuteDeleteAsync();
        await _db.ClientAliases.ExecuteDeleteAsync();

        await _db.Clients.ExecuteDeleteAsync();

        return NoContent();
    }
}
