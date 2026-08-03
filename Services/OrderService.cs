using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public class OrderService : IOrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task SyncOrderExpirationsAsync(int clientId)
    {
        try
        {
            var client = await _db.Clients.FindAsync(clientId);
            if (client == null) return;

            var pendingOrders = await _db.Orders
                .Where(o => o.ClientId == clientId && o.Status == OrderStatus.Pending)
                .ToListAsync();

            if (!pendingOrders.Any()) return;

            foreach (var order in pendingOrders)
            {
                var dates = CalculateOrderDates(client.Type, order.CreatedAt, order.ScheduledDeliveryDate);
                order.ExpiresAt = dates.ExpiresAt;
                order.ScheduledDeliveryDate = dates.ScheduledDeliveryDate;
            }

            // No llamamos SaveChangesAsync aquí — el llamador decide cuándo guardar
            // para permitir agrupar con otras operaciones en la misma transacción.
        }
        catch (Exception ex)
        {
            // Log exception here in a real scenario
            Console.WriteLine($"Error syncing order expirations for client {clientId}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public (DateTime ExpiresAt, DateTime ScheduledDeliveryDate) CalculateOrderDates(string clientType, DateTime createdAt, DateTime? manualDate = null)
    {
        var mexicoZone = BackendExtensions.GetMexicoZone();

        if (manualDate.HasValue)
        {
            // Si hay fecha manual (asumimos que viene como Date local sin hora)
            DateTime localDelivery;
            if (manualDate.Value.Kind == DateTimeKind.Utc)
            {
                localDelivery = TimeZoneInfo.ConvertTimeFromUtc(manualDate.Value, mexicoZone).Date;
            }
            else
            {
                localDelivery = manualDate.Value.Date;
            }

            // El vencimiento es 2 días después de la entrega (martes 23:59 si
            // la entrega es domingo, pero aplica a cualquier día manual).
            var localExpiration = localDelivery.AddDays(2);

            return (
                TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone),
                TimeZoneInfo.ConvertTimeToUtc(localDelivery, mexicoZone)
            );
        }
        else
        {
            // Regla de negocio:
            //   • Entrega programada: depende del tipo de clienta (Nueva = próximo
            //     domingo; Frecuente/VIP = segundo domingo).
            //   • Vigencia del enlace: 2 días después de la entrega (martes 23:59
            //     hora México cuando la entrega es domingo).
            var localDelivery = ComputeLocalDeliveryDate(clientType, createdAt);
            var localExpiration = localDelivery.AddDays(2);

            return (
                TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone),
                TimeZoneInfo.ConvertTimeToUtc(localDelivery, mexicoZone)
            );
        }
    }

    /// <inheritdoc />
    public DateTime CalculateExpiration(string clientType, DateTime createdAt)
    {
        var mexicoZone = BackendExtensions.GetMexicoZone();

        // Enforce UTC kind to prevent ArgumentException from TimeZoneInfo.ConvertTimeFromUtc
        if (createdAt.Kind == DateTimeKind.Unspecified)
        {
            createdAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);
        }

        // Vigencia: la entrega depende del tipo de clienta (Nueva = próximo domingo;
        // Frecuente/VIP = segundo domingo) y el enlace expira 2 días después.
        var localDelivery = ComputeLocalDeliveryDate(clientType, createdAt);
        var localExpiration = localDelivery.AddDays(2);

        return TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone);
    }

    /// <summary>
    /// Fecha (sin hora, hora local de México) de la entrega programada según el tipo
    /// de clienta:
    ///   • Nueva:           el próximo domingo.
    ///   • Frecuente / VIP: el segundo domingo (próximo domingo + 7 días).
    /// </summary>
    private static DateTime ComputeLocalDeliveryDate(string? clientType, DateTime createdAtUtc)
    {
        var localDelivery = NextSunday(createdAtUtc);
        if (IsFrequentType(clientType)) localDelivery = localDelivery.AddDays(7);
        return localDelivery;
    }

    /// <summary>Frecuente y VIP comparten regla de entrega (segundo domingo).</summary>
    private static bool IsFrequentType(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType)) return false;
        return clientType.Trim().Equals("Frecuente", StringComparison.OrdinalIgnoreCase)
            || clientType.Trim().Equals("VIP", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Estados en los que un pedido ya "tocó" logística y no debe tocarse con una fusión.</summary>
    private static readonly OrderStatus[] NotMergeableStatuses =
    {
        OrderStatus.InRoute, OrderStatus.Delivered, OrderStatus.NotDelivered,
        OrderStatus.Canceled, OrderStatus.Shipped
    };

    /// <inheritdoc />
    public async Task<OrderMergeResult> MergeOrdersAsync(int targetOrderId, int sourceOrderId)
    {
        if (targetOrderId == sourceOrderId)
            return OrderMergeResult.Fail("Un pedido no se puede fusionar consigo mismo.");

        var target = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .Include(o => o.Packages)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == targetOrderId);
        if (target == null) return OrderMergeResult.Fail("El pedido destino no existe.");

        var source = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .Include(o => o.Packages)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == sourceOrderId);
        if (source == null) return OrderMergeResult.Fail("El pedido de origen no existe.");

        if (source.MergedIntoOrderId.HasValue)
            return OrderMergeResult.Fail($"El pedido #{source.Id} ya se había fusionado antes con el #{source.MergedIntoOrderId}.");
        if (target.MergedIntoOrderId.HasValue)
            return OrderMergeResult.Fail($"El pedido #{target.Id} ya no está vigente: se fusionó con el #{target.MergedIntoOrderId}. Usa ese en su lugar.");

        if (NotMergeableStatuses.Contains(source.Status))
            return OrderMergeResult.Fail($"El pedido #{source.Id} está '{source.Status}' y ya no se puede fusionar (solo pedidos Pendientes, Confirmados o Pospuestos).");
        if (NotMergeableStatuses.Contains(target.Status))
            return OrderMergeResult.Fail($"El pedido #{target.Id} está '{target.Status}' y no puede recibir una fusión (solo pedidos Pendientes, Confirmados o Pospuestos).");

        if (source.Packages.Any())
            return OrderMergeResult.Fail($"El pedido #{source.Id} ya tiene bolsas/QR generados; quítalas antes de fusionarlo para no perder el rastro de esos códigos.");

        bool crossClient = source.ClientId != target.ClientId;
        int itemsMoved = source.Items.Count;
        decimal amountMoved = source.Subtotal;
        decimal paymentsMoved = source.Payments.Sum(p => p.Amount);

        foreach (var item in source.Items.ToList())
        {
            item.OrderId = target.Id;
            item.OriginalOrderId ??= source.Id;
            if (crossClient)
            {
                item.OriginalClientId ??= source.ClientId;
                item.OriginalClientName ??= source.Client?.Name;
            }
            target.Items.Add(item);
        }

        foreach (var payment in source.Payments.ToList())
        {
            payment.OrderId = target.Id;
            target.Payments.Add(payment);
        }

#pragma warning disable CS0618 // AdvancePayment es legacy pero aún puede traer saldo real en pedidos viejos
        if (source.AdvancePayment > 0)
        {
            paymentsMoved += source.AdvancePayment;
            target.AdvancePayment += source.AdvancePayment;
            source.AdvancePayment = 0;
        }
#pragma warning restore CS0618

        target.Subtotal = target.Items.Sum(i => i.LineTotal);
        target.Total = Math.Max(0, target.Subtotal + target.ShippingCost - target.DiscountAmount);

        source.Subtotal = 0;
        source.Total = 0;
        source.Status = OrderStatus.Canceled;
        source.MergedIntoOrderId = target.Id;
        source.MergedAt = DateTime.UtcNow;

        _db.OrderMergeAudits.Add(new OrderMergeAudit
        {
            SourceOrderId = source.Id,
            SourceClientId = source.ClientId,
            SourceClientName = source.Client?.Name ?? "",
            TargetOrderId = target.Id,
            TargetClientId = target.ClientId,
            TargetClientName = target.Client?.Name ?? "",
            ItemsMoved = itemsMoved,
            AmountMoved = amountMoved,
            PaymentsMoved = paymentsMoved,
        });

        await _db.SaveChangesAsync();

        return OrderMergeResult.Ok(target, itemsMoved);
    }

    /// <summary>
    /// Devuelve la fecha (sin hora) del PRÓXIMO domingo en hora local de México a
    /// partir de la fecha/hora UTC dada. Si la fecha de creación ya es domingo,
    /// cuenta como pasado y devuelve el domingo siguiente (la entrega nunca es "hoy").
    /// </summary>
    private static DateTime NextSunday(DateTime createdAtUtc)
    {
        var mexicoZone = BackendExtensions.GetMexicoZone();
        if (createdAtUtc.Kind == DateTimeKind.Unspecified)
        {
            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        }
        var localCreated = TimeZoneInfo.ConvertTimeFromUtc(createdAtUtc, mexicoZone).Date;

        // DayOfWeek.Sunday = 0. Si hoy es domingo, (7-0)%7 = 0 → se fuerza a 7 para
        // caer en el domingo siguiente.
        int daysUntilSunday = (7 - (int)localCreated.DayOfWeek) % 7;
        if (daysUntilSunday == 0) daysUntilSunday = 7;
        return localCreated.AddDays(daysUntilSunday);
    }
}
