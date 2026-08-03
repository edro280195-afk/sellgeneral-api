using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>Resultado de <see cref="IOrderService.MergeOrdersAsync"/>. Error != null cuando Success es false.</summary>
public record OrderMergeResult(bool Success, string? Error, Order? MergedOrder, int ItemsMoved)
{
    public static OrderMergeResult Fail(string error) => new(false, error, null, 0);
    public static OrderMergeResult Ok(Order mergedOrder, int itemsMoved) => new(true, null, mergedOrder, itemsMoved);
}

public interface IOrderService
{
    /// <summary>
    /// Recalculates and updates expiration dates for all Pending orders
    /// of the given client, based on their current Type (Nueva / Frecuente).
    /// </summary>
    Task SyncOrderExpirationsAsync(int clientId);

    /// <summary>
    /// Calculates the expiration date based on business rules.
    /// The link lives 2 days after the scheduled delivery (Tuesday 23:59 local
    /// when delivery is on Sunday).
    /// </summary>
    DateTime CalculateExpiration(string clientType, DateTime createdAt);

    /// <summary>
    /// Calculates both Expiration and Scheduled Delivery Date.
    /// If manualDate is provided: ExpiresAt = manualDate + 2 days.
    /// If not: ScheduledDeliveryDate = next Sunday (Nueva) or second Sunday
    /// (Frecuente/VIP), ExpiresAt = that Sunday + 2 days. If createdAt itself
    /// falls on a Sunday, it counts as already past and rolls to the following one.
    /// </summary>
    (DateTime ExpiresAt, DateTime ScheduledDeliveryDate) CalculateOrderDates(string clientType, DateTime createdAt, DateTime? manualDate = null);

    /// <summary>
    /// Fusiona <paramref name="sourceOrderId"/> DENTRO de <paramref name="targetOrderId"/>: mueve
    /// todos sus artículos y pagos, recalcula totales del destino, y deja el pedido de origen como
    /// cascarón Cancelado (nunca se borra) apuntando a <see cref="Order.MergedIntoOrderId"/>.
    /// Funciona tanto si ambos pedidos son de la misma clienta (duplicados) como de clientas
    /// distintas (ej. "agrega lo de mi hija a mi bolsa") — en ese segundo caso, cada artículo
    /// movido queda etiquetado con su clienta original vía <see cref="OrderItem.OriginalClientName"/>
    /// para que no se pierda de quién era, aunque viajen juntos en una sola entrega/bolsa.
    /// No hace nada si alguno de los dos pedidos ya está entregado, cancelado, en ruta, o ya
    /// fue fusionado antes — en esos casos regresa Success=false con un mensaje explicativo.
    /// </summary>
    Task<OrderMergeResult> MergeOrdersAsync(int targetOrderId, int sourceOrderId);
}
