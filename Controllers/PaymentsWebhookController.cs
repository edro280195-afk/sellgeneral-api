using EntregasApi.Data;
using EntregasApi.Hubs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    private readonly IPushNotificationService _push;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ICurrentTenant _tenant;
    private readonly IMercadoPagoSubscriptionService _mpPlatform;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaymentsWebhookController> _logger;

    public PaymentsWebhookController(
        AppDbContext db,
        IHubContext<DeliveryHub> hub,
        IPushNotificationService push,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ICurrentTenant tenant,
        IMercadoPagoSubscriptionService mpPlatform,
        TimeProvider timeProvider,
        ILogger<PaymentsWebhookController> logger)
    {
        _db = db;
        _hub = hub;
        _push = push;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _tenant = tenant;
        _mpPlatform = mpPlatform;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [HttpPost]
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

        // Pagos one-time (pedidos y tandas): se cobran con la credencial del
        // tenant, igual que antes de 1.3.
        if (!string.Equals(notification.Type, "payment", StringComparison.OrdinalIgnoreCase))
        {
            return Ok();
        }

        return await HandleOneTimePaymentAsync(notification.Data.Id);
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
            return string.IsNullOrWhiteSpace(_config["Platform:MercadoPago:WebhookSecret"]) ||
                   _config["Platform:MercadoPago:WebhookSecret"] == "dummy";
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

    private async Task<IActionResult> HandleOneTimePaymentAsync(string paymentId)
    {
        var mpAccessToken = _config["MercadoPago:AccessToken"];
        if (string.IsNullOrEmpty(mpAccessToken))
        {
            Console.WriteLine("[Webhook] Error: MercadoPago:AccessToken no configurado.");
            return Ok();
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {mpAccessToken}");

        try
        {
            var response = await client.GetAsync($"https://api.mercadopago.com/v1/payments/{paymentId}");
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Webhook] Error al consultar pago {paymentId}: {response.StatusCode}");
                return Ok();
            }

            var rawBody = await response.Content.ReadAsStringAsync();
            var mpPayment = System.Text.Json.JsonSerializer.Deserialize<MpPaymentDetail>(rawBody, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (mpPayment == null || string.IsNullOrEmpty(mpPayment.ExternalReference))
            {
                Console.WriteLine($"[Webhook] Pago {paymentId} no tiene external_reference.");
                return Ok();
            }

            if (mpPayment.ExternalReference.StartsWith("order_"))
            {
                await ProcessOrderPayment(mpPayment);
            }
            else if (mpPayment.ExternalReference.StartsWith("tanda_"))
            {
                await ProcessTandaPayment(mpPayment);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Webhook] Excepción procesando pago {paymentId}: {ex.Message}");
        }

        return Ok();
    }

    private async Task ProcessTandaPayment(MpPaymentDetail mpPayment)
    {
        // Format: tanda_{tandaId}_{participantId}_{weekNumber}
        var parts = mpPayment.ExternalReference?.Split('_');
        if (parts == null || parts.Length < 4) return;

        if (!Guid.TryParse(parts[1], out Guid tandaId) || 
            !Guid.TryParse(parts[2], out Guid participantId) ||
            !int.TryParse(parts[3], out int week))
            return;

        if (mpPayment.Status != "approved") return;

        var participant = await _db.TandaParticipants
            .Include(p => p.Payments)
            .Include(p => p.Tanda)
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.Id == participantId);

        if (participant == null) return;

        // Verificar duplicados
        var existing = participant.Payments.Any(p => p.WeekNumber == week && p.Notes != null && p.Notes.Contains($"MP#{mpPayment.Id}"));
        if (existing) return;

        var payment = new TandaPayment
        {
            ParticipantId = participant.Id,
            WeekNumber = week,
            AmountPaid = mpPayment.TransactionAmount,
            PaymentDate = DateTime.UtcNow,
            IsVerified = true,
            Notes = $"MP#{mpPayment.Id} (Tarjeta/Webhook)"
        };

        _db.TandaPayments.Add(payment);
        await _db.SaveChangesAsync();

        // Notificar a Admins
        var clientName = participant.Client?.Name ?? "Participante";
        await _hub.Clients.Group(SignalRGroupNames.Admins(_tenant.ActiveBusinessId)).SendAsync("DeliveryUpdate", new {
            TandaId = tandaId,
            ParticipantName = clientName,
            Amount = mpPayment.TransactionAmount,
            Type = "tanda_card_payment_webhook"
        });

        await _push.SendNotificationToAdminsAsync(
            $"💎 Pago Tanda (Webhook): ${mpPayment.TransactionAmount:F2}",
            $"{clientName} pagó la semana {week} de {participant.Tanda?.Name ?? "Tanda"}.",
            tag: "tanda-payment-webhook"
        );
    }

    private async Task ProcessOrderPayment(MpPaymentDetail mpPayment)
    {
        if (!int.TryParse(mpPayment.ExternalReference.Replace("order_", ""), out int orderId))
            return;

        var order = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
        {
            Console.WriteLine($"[Webhook] Pedido #{orderId} no encontrado.");
            return;
        }

        // Verificar si ya registramos este ID de pago
        var existingPayment = order.Payments?.FirstOrDefault(p => p.Notes != null && p.Notes.Contains($"MP#{mpPayment.Id}"));

        if (mpPayment.Status == "approved")
        {
            if (existingPayment == null)
            {
                // Es un pago nuevo aprobado (quizás la clienta cerró el navegador antes)
                var payment = new OrderPayment
                {
                    OrderId = order.Id,
                    Amount = mpPayment.TransactionAmount,
                    Method = "Tarjeta",
                    RegisteredBy = "Sistema (Webhook)",
                    Notes = $"MP#{mpPayment.Id} | {mpPayment.StatusDetail} (Auto)"
                };
                _db.OrderPayments.Add(payment);
                
                // Confirmar pedido si estaba pendiente
                if (order.Status is Models.OrderStatus.Pending or Models.OrderStatus.Postponed)
                {
                    order.Status = Models.OrderStatus.Confirmed;
                }

                await _db.SaveChangesAsync();

                // Notificar a Admins
                await NotifyAdmins(order, mpPayment.TransactionAmount, "aprobado");
            }
            else if (existingPayment.Notes != null && !existingPayment.Notes.Contains("approved"))
            {
                // El pago existía (ej. estaba 'in_process') y ahora se aprobó
                existingPayment.Notes = $"MP#{mpPayment.Id} | approved (Webhook)";
                
                if (order.Status is Models.OrderStatus.Pending or Models.OrderStatus.Postponed)
                {
                    order.Status = Models.OrderStatus.Confirmed;
                }

                await _db.SaveChangesAsync();
                await NotifyAdmins(order, mpPayment.TransactionAmount, "confirmado (era pendiente)");
            }
        }
        else if (mpPayment.Status == "rejected")
        {
            if (existingPayment != null)
            {
                existingPayment.Notes = $"MP#{mpPayment.Id} | rejected (Webhook)";
                await _db.SaveChangesAsync();
                // Opcional: Notificar rechazo
            }
        }
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

    private async Task NotifyAdmins(Order order, decimal amount, string action)
    {
        var clientName = order.Client?.Name ?? "Clienta";

        // SignalR
        await _hub.Clients.Group(SignalRGroupNames.Admins(_tenant.ActiveBusinessId)).SendAsync("DeliveryUpdate", new
        {
            OrderId = order.Id,
            Status = order.Status.ToString(),
            ChangeType = "card_payment_webhook",
            ClientName = clientName,
            AmountPaid = amount
        });

        // Push
        await _push.SendNotificationToAdminsAsync(
            $"💳 Pago {action}: ${amount:F2}",
            $"Pedido #{order.Id} de {clientName} actualizado vía Mercado Pago.",
            tag: "card-payment-webhook"
        );
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
