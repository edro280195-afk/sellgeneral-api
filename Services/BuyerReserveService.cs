using System.Collections.Concurrent;
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

    // Candado en proceso por (BusinessId, ProductId): serializa los
    // "Apartar" concurrentes sobre el MISMO producto para que una ráfaga de
    // toques durante un live (varias compradoras apartando el mismo
    // artículo casi a la vez) no sobrevenda el último disponible. `static`
    // a propósito — tiene que sobrevivir entre instancias Scoped de este
    // service, una por request, para de verdad serializar entre requests.
    // Nota: solo protege dentro de este proceso; si el API llegara a correr
    // en varias instancias a la vez, esto dejaría de alcanzar por sí solo.
    private static readonly ConcurrentDictionary<(int BusinessId, int ProductId), SemaphoreSlim> ReserveGates = new();

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

    private static SemaphoreSlim GateFor(int businessId, int productId) =>
        ReserveGates.GetOrAdd((businessId, productId), static _ => new SemaphoreSlim(1, 1));

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

        // 2. Candado en proceso por producto: dos "Apartar" simultáneos
        //    sobre el mismo producto se serializan aquí, así ninguno lee
        //    stock disponible que el otro ya se está por quedar. Sin esto,
        //    una ráfaga de toques durante un live (varias compradoras
        //    apartando el mismo artículo casi a la vez) podría sobrevender
        //    el último disponible — Product.Stock no se decrementa al
        //    apartar (eso lo sigue haciendo PosService al pagar), así que
        //    el único freno real es contar también lo que ya está apartado
        //    y sin pagar.
        var gate = GateFor(request.BusinessId, request.ProductId);
        await gate.WaitAsync(cancellationToken);
        Order order;
        try
        {
            var product = await _db.Products.AsNoTracking().IgnoreQueryFilters()
                .Where(p => p.Id == request.ProductId && p.BusinessId == request.BusinessId)
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

            var now = DateTime.UtcNow;
            var reservedElsewhere = await _db.Orders.IgnoreQueryFilters()
                .Where(o => o.BusinessId == request.BusinessId
                            && o.Status == OrderStatus.Pending
                            && o.Tags == "apartado"
                            && (o.ExpiresAt == null || o.ExpiresAt > now))
                .SelectMany(o => o.Items)
                .Where(oi => oi.ProductId == request.ProductId)
                .SumAsync(oi => (int?)oi.Quantity, cancellationToken) ?? 0;

            var available = product.Stock - reservedElsewhere;
            if (available < request.Quantity)
            {
                throw new ReserveBadRequestException(
                    $"Solo hay {Math.Max(0, available)} disponibles de este producto.");
            }

            // 3. Calcular fechas con las mismas reglas que un Order manual.
            var dates = _orderService.CalculateOrderDates(
                myClient.Type, DateTime.UtcNow);

            // 4. Crear el Order (reusa el patrón de apartado manual).
            order = new Order
            {
                BusinessId = request.BusinessId,
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
                BusinessId = request.BusinessId,
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = request.Quantity,
                UnitPrice = product.Price,
                LineTotal = product.Price * request.Quantity,
            });

            _db.Orders.Add(order);
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        // 5. Notificar a la vendedora (push al admin del tenant).
        try
        {
            await _push.SendNotificationToAdminsAsync(
                title: "Nuevo apartado 💖",
                message: $"{myClient.Name} apartó {order.Items.First().ProductName}",
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
