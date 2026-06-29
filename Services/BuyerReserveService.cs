using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerReserveService
{
    /// <summary>
    /// Crea un Order con `Status = Pending` y `OrderType = PickUp` para
    /// que la compradora aparta un producto. Lanza excepciones específicas
    /// (`ReserveNotFoundException`, `ReserveBadRequestException`) que el
    /// controller traduce a 404 / 400.
    /// </summary>
    Task<BuyerOrderDto> ReserveAsync(
        int accountId,
        ReserveProductRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Excepción que se traduce a 404 en el controller. Tienda no existe, la
/// compradora no tiene Client en ella, o el producto no existe.
/// </summary>
public class ReserveNotFoundException : Exception
{
    public ReserveNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Excepción que se traduce a 400 en el controller. Producto inactivo o
/// stock insuficiente.
/// </summary>
public class ReserveBadRequestException : Exception
{
    public ReserveBadRequestException(string message) : base(message) { }
}

/// <summary>
/// Crea pedidos de apartado (Status=Pending, OrderType=PickUp) desde la app
/// de la compradora. Sigue el patrón cross-tenant por AccountId del
/// resto de la Fase 2. NO decrementa stock (eso lo sigue haciendo
/// `PosService.PayPosOrderAsync`); solo valida que haya stock
/// disponible al momento del apartado.
/// </summary>
public class BuyerReserveService : IBuyerReserveService
{
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;
    private readonly IOrderService _orderService;
    private readonly IPushNotificationService _push;

    public BuyerReserveService(
        AppDbContext db,
        IOrderService orderService,
        IPushNotificationService push)
    {
        _db = db;
        _orderService = orderService;
        _push = push;
    }

    public async Task<BuyerOrderDto> ReserveAsync(
        int accountId,
        ReserveProductRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Quantity < 1)
        {
            throw new ReserveBadRequestException("La cantidad debe ser al menos 1.");
        }

        // 1. Resolver la tienda (cross-tenant, scoping por AccountId para
        //    saber que la compradora tiene Client aquí).
        var myClient = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId
                        && c.BusinessId == request.BusinessId)
            .Select(c => new { c.Id, c.Type, c.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (myClient is null)
        {
            throw new ReserveNotFoundException("Esta tienda no está en tu cuenta.");
        }

        // 2. Resolver el producto (debe existir, estar activo, y pertenecer
        //    a esta tienda).
        var product = await _db.Products.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.Id == request.ProductId
                        && p.BusinessId == request.BusinessId)
            .Select(p => new { p.Id, p.Name, p.Price, p.Stock, p.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            throw new ReserveNotFoundException("Este producto no está disponible.");
        }

        if (!product.IsActive)
        {
            throw new ReserveBadRequestException("Este producto ya no está disponible.");
        }

        if (product.Stock < request.Quantity)
        {
            throw new ReserveBadRequestException(
                $"Solo hay {product.Stock} disponibles de este producto.");
        }

        // 3. Calcular fechas con las mismas reglas que un Order manual.
        var dates = _orderService.CalculateOrderDates(
            myClient.Type, DateTime.UtcNow);

        // 4. Crear el Order (reusa el patrón de LiveCaptureService.ConfirmCandidate).
        var order = new Order
        {
            ClientId = myClient.Id,
            Status = OrderStatus.Pending,
            OrderType = OrderType.PickUp,
            Subtotal = product.Price * request.Quantity,
            ShippingCost = 0m,
            Total = product.Price * request.Quantity,
            AccessToken = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = dates.ExpiresAt,
            ScheduledDeliveryDate = dates.ScheduledDeliveryDate,
            Tags = "apartado",
        };
        order.Items.Add(new OrderItem
        {
            ProductId = product.Id,
            ProductName = product.Name,
            Quantity = request.Quantity,
            UnitPrice = product.Price,
            LineTotal = product.Price * request.Quantity,
        });

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(cancellationToken);

        // 5. Notificar a la vendedora (push al admin del tenant).
        try
        {
            await _push.SendNotificationToAdminsAsync(
                title: "Nuevo apartado 💖",
                message: $"{myClient.Name} apartó {product.Name}",
                url: "/orders",
                tag: "reserve");
        }
        catch
        {
            // El push no debe tumbar la operación. Si falla, el order ya
            // está creado y la vendedora lo verá al refrescar su panel.
        }

        // 6. Resolver marca/logo para la respuesta.
        var biz = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == request.BusinessId)
            .Select(b => new { b.Name, b.BrandPrimaryColor, b.LogoUrl })
            .FirstOrDefaultAsync(cancellationToken);

        return new BuyerOrderDto(
            OrderId: order.Id,
            BusinessId: order.BusinessId,
            BusinessName: biz?.Name ?? "",
            BrandPrimaryColor: !string.IsNullOrWhiteSpace(biz?.BrandPrimaryColor)
                ? biz!.BrandPrimaryColor
                : DefaultBrandColor,
            LogoUrl: biz?.LogoUrl,
            Status: order.Status.ToString(),
            ItemsCount: order.Items.Count,
            Total: order.Total,
            AccessToken: order.AccessToken,
            CreatedAt: order.CreatedAt,
            ScheduledDeliveryDate: order.ScheduledDeliveryDate);
    }
}
