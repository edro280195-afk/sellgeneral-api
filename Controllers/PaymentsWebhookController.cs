using EntregasApi.Data;
using EntregasApi.Hubs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/payments/webhook")]
[AllowAnonymous]
public class PaymentsWebhookController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly IMercadoPagoSubscriptionService _mpPlatform;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaymentsWebhookController> _logger;
    private readonly IWebHostEnvironment _environment;

    public PaymentsWebhookController(
        AppDbContext db,
        IHubContext<DeliveryHub> hub,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        IMercadoPagoSubscriptionService mpPlatform,
        TimeProvider timeProvider,
        ILogger<PaymentsWebhookController> logger,
        IWebHostEnvironment environment)
    {
        _db = db;
        _hub = hub;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _mpPlatform = mpPlatform;
        _timeProvider = timeProvider;
        _logger = logger;
        _environment = environment;
    }

    [HttpPost]
    [EnableRateLimiting(SecurityRateLimitPolicies.Webhook)]
    public async Task<IActionResult> HandleWebhook([FromBody] MpWebhookNotification notification)
    {
        if (notification.Data?.Id == null || string.IsNullOrWhiteSpace(notification.Type))
        {
            return Ok();
        }

        // Preapproval y authorized_payment son eventos de la suscripcion de
        // PLATAFORMA (no del tenant). Validamos la firma x-signature si esta
        // configurada la webhook secret de plataforma.
        if (string.Equals(notification.Type, "preapproval", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(notification.Type, "authorized_payment", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(notification.Type, "subscription_preapproval", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(notification.Type, "subscription_authorized_payment", StringComparison.OrdinalIgnoreCase))
        {
            if (!ValidatePlatformSignature(notification))
            {
                _logger.LogWarning(
                    "[Webhook] Firma de plataforma invalida para type={Type} id={Id}",
                    notification.Type,
                    notification.Data.Id);
                return Unauthorized();
            }

            return await HandlePlatformWebhookAsync(notification, default);
        }

        // Pagos one-time (pedidos y tandas): se consulta MP con la credencial
        // del negocio indicada en notification_url (?businessId=), nunca con
        // MercadoPago:AccessToken global.
        if (!string.Equals(notification.Type, "payment", StringComparison.OrdinalIgnoreCase))
        {
            return Ok();
        }

        return await HandleOneTimePaymentAsync(notification.Data.Id, HttpContext.RequestAborted);
    }

    private bool ValidatePlatformSignature(MpWebhookNotification notification)
    {
        var requestId = Request.Headers["x-request-id"].ToString();
        var signature = Request.Headers["x-signature"].ToString();
        if (string.IsNullOrWhiteSpace(signature))
        {
            // Si no hay firma configurada en el server, permitimos. Esto
            // mantiene DEV funcionando sin secret; en produccion el secret
            // SIEMPRE debe existir.
            var configuredSecret = _config["Platform:MercadoPago:WebhookSecret"];
            return _environment.IsDevelopment() &&
                   (string.IsNullOrWhiteSpace(configuredSecret) ||
                    configuredSecret == "dummy");
        }

        return _mpPlatform.ValidateWebhookSignature(
            requestId,
            signature,
            notification.Data?.Id,
            _timeProvider.GetUtcNow());
    }

    private async Task<IActionResult> HandlePlatformWebhookAsync(
        MpWebhookNotification notification,
        CancellationToken cancellationToken)
    {
        var resourceId = notification.Data!.Id!;
        try
        {
            if (notification.Type.StartsWith("preapproval", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessPreapprovalEventAsync(resourceId, cancellationToken);
            }
            else
            {
                await ProcessAuthorizedPaymentEventAsync(resourceId, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Webhook] Excepcion procesando evento plataforma {Type} {Id}",
                notification.Type, resourceId);
        }
        return Ok();
    }

    private async Task<IActionResult> HandleOneTimePaymentAsync(
        string paymentId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(Request.Query["businessId"].ToString(), out var businessId))
        {
            _logger.LogWarning(
                "[Webhook] Evento payment {PaymentId} sin businessId. Se ignora para evitar usar credenciales globales.",
                paymentId);
            return Ok();
        }

        var business = await _db.Businesses
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == businessId, cancellationToken);
        if (business is null || string.IsNullOrWhiteSpace(business.MercadoPagoAccessToken))
        {
            _logger.LogWarning(
                "[Webhook] Evento payment {PaymentId} para business {BusinessId} sin token de Mercado Pago.",
                paymentId,
                businessId);
            return Ok();
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {business.MercadoPagoAccessToken}");

        try
        {
            var response = await client.GetAsync(
                $"https://api.mercadopago.com/v1/payments/{paymentId}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[Webhook] Error al consultar pago {PaymentId} de business {BusinessId}: {StatusCode}",
                    paymentId,
                    businessId,
                    response.StatusCode);
                return Ok();
            }

            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var mpPayment = System.Text.Json.JsonSerializer.Deserialize<MpPaymentDetail>(
                rawBody,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (mpPayment == null || string.IsNullOrWhiteSpace(mpPayment.ExternalReference))
            {
                _logger.LogWarning("[Webhook] Pago {PaymentId} no tiene external_reference.", paymentId);
                return Ok();
            }

            if (mpPayment.ExternalReference.StartsWith("order_", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessOrderPaymentAsync(mpPayment, businessId, cancellationToken);
            }
            else if (mpPayment.ExternalReference.StartsWith("tanda_", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessTandaPaymentAsync(mpPayment, businessId, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] Excepcion procesando pago {PaymentId}", paymentId);
        }

        return Ok();
    }

    private async Task ProcessTandaPaymentAsync(
        MpPaymentDetail mpPayment,
        int businessId,
        CancellationToken cancellationToken)
    {
        var parts = mpPayment.ExternalReference?.Split('_');
        if (parts == null || parts.Length < 4) return;

        if (!Guid.TryParse(parts[1], out var tandaId) ||
            !Guid.TryParse(parts[2], out var participantId) ||
            !int.TryParse(parts[3], out var week))
        {
            return;
        }

        if (!string.Equals(mpPayment.Status, "approved", StringComparison.OrdinalIgnoreCase)) return;

        var participant = await _db.TandaParticipants
            .IgnoreQueryFilters()
            .Include(p => p.Tanda)
            .Include(p => p.Client)
            .FirstOrDefaultAsync(
                p => p.Id == participantId && p.BusinessId == businessId,
                cancellationToken);

        if (participant == null) return;

        var existing = await _db.TandaPayments
            .IgnoreQueryFilters()
            .AnyAsync(
                p => p.BusinessId == businessId &&
                     p.ParticipantId == participantId &&
                     p.WeekNumber == week &&
                     p.Notes != null &&
                     p.Notes.Contains($"MP#{mpPayment.Id}"),
                cancellationToken);
        if (existing) return;

        var payment = new TandaPayment
        {
            BusinessId = businessId,
            ParticipantId = participant.Id,
            WeekNumber = week,
            AmountPaid = mpPayment.TransactionAmount,
            PaymentDate = DateTime.UtcNow,
            IsVerified = true,
            Notes = $"MP#{mpPayment.Id} (Tarjeta/Webhook)"
        };

        _db.TandaPayments.Add(payment);
        await _db.SaveChangesAsync(cancellationToken);

        var clientName = participant.Client?.Name ?? "Participante";
        await _hub.Clients.Group(SignalRGroupNames.Admins(businessId)).SendAsync("DeliveryUpdate", new
        {
            TandaId = tandaId,
            ParticipantName = clientName,
            Amount = mpPayment.TransactionAmount,
            Type = "tanda_card_payment_webhook"
        }, cancellationToken);
    }

    private async Task ProcessOrderPaymentAsync(
        MpPaymentDetail mpPayment,
        int businessId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(mpPayment.ExternalReference?.Replace("order_", ""), out var orderId))
        {
            return;
        }

        var order = await _db.Orders
            .IgnoreQueryFilters()
            .Include(o => o.Client)
            .FirstOrDefaultAsync(
                o => o.Id == orderId && o.BusinessId == businessId,
                cancellationToken);

        if (order == null)
        {
            _logger.LogWarning("[Webhook] Pedido #{OrderId} no encontrado para business {BusinessId}.", orderId, businessId);
            return;
        }

        var existingPayment = await _db.OrderPayments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.BusinessId == businessId &&
                     p.OrderId == order.Id &&
                     p.Notes != null &&
                     p.Notes.Contains($"MP#{mpPayment.Id}"),
                cancellationToken);

        if (string.Equals(mpPayment.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            if (existingPayment == null)
            {
                var payment = new OrderPayment
                {
                    BusinessId = businessId,
                    OrderId = order.Id,
                    Amount = mpPayment.TransactionAmount,
                    Method = "Tarjeta",
                    RegisteredBy = "Sistema (Webhook)",
                    Notes = $"MP#{mpPayment.Id} | {mpPayment.StatusDetail} (Auto)"
                };
                _db.OrderPayments.Add(payment);

                if (order.Status is Models.OrderStatus.Pending or Models.OrderStatus.Postponed)
                {
                    order.Status = Models.OrderStatus.Confirmed;
                }

                await _db.SaveChangesAsync(cancellationToken);
                await NotifyAdminsAsync(order, mpPayment.TransactionAmount, businessId, "aprobado", cancellationToken);
            }
            else if (existingPayment.Notes != null && !existingPayment.Notes.Contains("approved"))
            {
                existingPayment.Notes = $"MP#{mpPayment.Id} | approved (Webhook)";

                if (order.Status is Models.OrderStatus.Pending or Models.OrderStatus.Postponed)
                {
                    order.Status = Models.OrderStatus.Confirmed;
                }

                await _db.SaveChangesAsync(cancellationToken);
                await NotifyAdminsAsync(order, mpPayment.TransactionAmount, businessId, "confirmado", cancellationToken);
            }
        }
        else if (string.Equals(mpPayment.Status, "rejected", StringComparison.OrdinalIgnoreCase) &&
                 existingPayment != null)
        {
            existingPayment.Notes = $"MP#{mpPayment.Id} | rejected (Webhook)";
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task NotifyAdminsAsync(
        Order order,
        decimal amount,
        int businessId,
        string action,
        CancellationToken cancellationToken)
    {
        var clientName = order.Client?.Name ?? "Clienta";

        await _hub.Clients.Group(SignalRGroupNames.Admins(businessId)).SendAsync("DeliveryUpdate", new
        {
            OrderId = order.Id,
            Status = order.Status.ToString(),
            ChangeType = "card_payment_webhook",
            ClientName = clientName,
            AmountPaid = amount,
            Action = action
        }, cancellationToken);
    }

    private async Task ProcessPreapprovalEventAsync(
        string preapprovalId,
        CancellationToken cancellationToken)
    {
        var preapproval = await _mpPlatform.GetPreapprovalAsync(preapprovalId, cancellationToken);
        if (preapproval is null)
        {
            _logger.LogWarning("[Webhook] Preapproval {Id} no encontrado en MP", preapprovalId);
            return;
        }

        var business = await _db.Businesses
            .FirstOrDefaultAsync(b => b.PreapprovalId == preapprovalId, cancellationToken);
        if (business is null)
        {
            _logger.LogWarning(
                "[Webhook] Preapproval {Id} no corresponde a ningun Business",
                preapprovalId);
            return;
        }

        business.PreapprovalStatus = preapproval.Status;
        business.PayerEmail = preapproval.PayerEmail ?? business.PayerEmail;

        switch (preapproval.Status?.ToLowerInvariant())
        {
            case "authorized":
                business.SubscriptionStatus = SubscriptionStatus.Active;
                business.CancellationEffectiveAt = null;
                business.TrialEndsAt = null;
                if (preapproval.NextPaymentDate is not null)
                {
                    business.CurrentPeriodEndsAt = preapproval.NextPaymentDate;
                }
                else if (business.CurrentPeriodEndsAt is null)
                {
                    business.CurrentPeriodEndsAt = SubscriptionController.ComputeNextPeriodEnd(
                        _timeProvider.GetUtcNow().UtcDateTime,
                        MonthsToPeriodicity(business.SubscriptionPeriodMonths));
                }
                break;

            case "paused":
                // Pausa: mantenemos el periodo actual pero marcamos PastDue
                // para que 1.2 aplique la gracia antes de bloquear.
                business.SubscriptionStatus = SubscriptionStatus.PastDue;
                break;

            case "cancelled":
                business.SubscriptionStatus = SubscriptionStatus.Canceled;
                // Si MP no reporta end_date, usamos la fecha actual como horizonte
                // para que la siguiente evaluacion perezosa (1.2) la bloquee.
                business.CancellationEffectiveAt = preapproval.EndDate
                    ?? business.CurrentPeriodEndsAt
                    ?? _timeProvider.GetUtcNow().UtcDateTime;
                break;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ProcessAuthorizedPaymentEventAsync(
        string authorizedPaymentId,
        CancellationToken cancellationToken)
    {
        var invoice = await _mpPlatform.GetAuthorizedPaymentAsync(authorizedPaymentId, cancellationToken);
        if (invoice is null)
        {
            _logger.LogWarning(
                "[Webhook] AuthorizedPayment {Id} no encontrado en MP",
                authorizedPaymentId);
            return;
        }

        if (string.IsNullOrWhiteSpace(invoice.PreapprovalId))
        {
            _logger.LogWarning(
                "[Webhook] AuthorizedPayment {Id} sin preapproval_id",
                authorizedPaymentId);
            return;
        }

        var business = await _db.Businesses
            .FirstOrDefaultAsync(b => b.PreapprovalId == invoice.PreapprovalId, cancellationToken);
        if (business is null)
        {
            _logger.LogWarning(
                "[Webhook] AuthorizedPayment {Id} apunta a preapproval {Pid} sin Business",
                authorizedPaymentId,
                invoice.PreapprovalId);
            return;
        }

        switch (invoice.Status?.ToLowerInvariant())
        {
            case "approved":
            case "processed":
                business.SubscriptionStatus = SubscriptionStatus.Active;
                business.CancellationEffectiveAt = null;
                if (invoice.DateCreated is not null)
                {
                    business.CurrentPeriodEndsAt = SubscriptionController.ComputeNextPeriodEnd(
                        invoice.DateCreated.Value,
                        MonthsToPeriodicity(business.SubscriptionPeriodMonths));
                }
                break;

            case "rejected":
            case "failed":
                business.SubscriptionStatus = SubscriptionStatus.PastDue;
                break;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static SubscriptionPeriodicity MonthsToPeriodicity(int months)
    {
        return months switch
        {
            3 => SubscriptionPeriodicity.Quarterly,
            12 => SubscriptionPeriodicity.Annual,
            _ => SubscriptionPeriodicity.Monthly
        };
    }

    // DTOs para el Webhook
    public class MpWebhookNotification
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("data")]
        public MpWebhookData? Data { get; set; }
    }

    public class MpWebhookData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
    }

    public class MpPaymentDetail
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("status_detail")]
        public string StatusDetail { get; set; } = "";

        [JsonPropertyName("external_reference")]
        public string? ExternalReference { get; set; }

        [JsonPropertyName("transaction_amount")]
        public decimal TransactionAmount { get; set; }
    }
}
