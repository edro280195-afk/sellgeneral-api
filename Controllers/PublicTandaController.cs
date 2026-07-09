using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EntregasApi.DTOs;
using EntregasApi.Services;
using EntregasApi.Data;
using EntregasApi.Hubs;
using EntregasApi.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/public-tanda")]
[AllowAnonymous] 
public class PublicTandaController : ControllerBase
{
    private const string CardPaymentsNotConfiguredMessage =
        "Esta tienda aun no tiene pagos con tarjeta configurados. Usa efectivo, transferencia u otro metodo acordado con la tienda.";

    private readonly ITandaService _tandaService;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IPushNotificationService _push;

    public PublicTandaController(
        ITandaService tandaService,
        AppDbContext db,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        IHubContext<DeliveryHub> hub,
        IPushNotificationService push)
    {
        _tandaService = tandaService;
        _db = db;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _hub = hub;
        _push = push;
    }

    [HttpGet("{token}")]
    [EnableRateLimiting(SecurityRateLimitPolicies.PublicTokenRead)]
    public async Task<IActionResult> GetTandaByToken(string token)
    {
        try
        {
            var tanda = await _tandaService.GetTandaByTokenAsync(token);
            
            if (tanda == null)
            {
                return NotFound(new { message = "Tanda no encontrada o enlace inválido." });
            }

            return Ok(tanda);
        }
        catch (Exception)
        {
            return BadRequest(new { message = "No se pudo consultar la tanda." });
        }
    }

    [HttpPost("{token}/payment/card")]
    [EnableRateLimiting(SecurityRateLimitPolicies.PublicTokenWrite)]
    public async Task<IActionResult> PayWithCard(string token, [FromBody] TandaCardPaymentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CardToken) ||
            string.IsNullOrWhiteSpace(req.PaymentMethodId) ||
            req.WeekNumber < 1)
        {
            return BadRequest(new { message = "Datos de pago incompletos." });
        }

        var tanda = await _db.Tandas
            .Include(t => t.Participants)
                .ThenInclude(p => p.Client)
            .FirstOrDefaultAsync(t => t.AccessToken == token);

        if (tanda == null) return NotFound("Tanda no encontrada.");

        var participant = tanda.Participants.FirstOrDefault(p => p.Id == req.ParticipantId);
        if (participant == null) return NotFound("Participante no encontrado en esta tanda.");

        var business = await _db.Businesses
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == tanda.BusinessId);
        if (business is null ||
            string.IsNullOrWhiteSpace(business.MercadoPagoAccessToken) ||
            string.IsNullOrWhiteSpace(business.MercadoPagoPublicKey))
        {
            return StatusCode(StatusCodes.Status409Conflict, new { message = CardPaymentsNotConfiguredMessage });
        }

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {business.MercadoPagoAccessToken}");
        httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        var paymentBody = new
        {
            transaction_amount = tanda.WeeklyAmount,
            token = req.CardToken,
            description = $"Tanda {tanda.Name} - Semana {req.WeekNumber}",
            installments = 1,
            payment_method_id = req.PaymentMethodId,
            payer = new { email = $"pagos+business-{business.Id}@sellgeneral.app" },
            external_reference = $"tanda_{tanda.Id}_{participant.Id}_{req.WeekNumber}",
            notification_url = BuildTenantPaymentWebhookUrl(business.Id),
            metadata = new
            {
                tanda_id = tanda.Id,
                participant_id = participant.Id,
                business_id = business.Id,
                week = req.WeekNumber,
                type = "tanda_payment"
            }
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", paymentBody);
            var rawBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, new
                {
                    message = ReadMercadoPagoError(rawBody)
                });
            }

            var mpResult = System.Text.Json.JsonSerializer.Deserialize<MpPaymentApiResponse>(
                rawBody,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (mpResult?.Status == "approved")
            {
                var payment = new TandaPayment
                {
                    BusinessId = tanda.BusinessId,
                    ParticipantId = participant.Id,
                    WeekNumber = req.WeekNumber,
                    AmountPaid = tanda.WeeklyAmount,
                    PaymentDate = DateTime.UtcNow,
                    IsVerified = true,
                    Notes = $"MP#{mpResult.Id} (Tarjeta)"
                };
                _db.TandaPayments.Add(payment);
                await _db.SaveChangesAsync();

                var clientName = participant.Client?.Name ?? "Participante";
                await _hub.Clients.Group(SignalRGroupNames.Admins(tanda.BusinessId)).SendAsync("DeliveryUpdate", new
                {
                    TandaId = tanda.Id,
                    ParticipantName = clientName,
                    Amount = tanda.WeeklyAmount,
                    Type = "tanda_card_payment"
                });

                await _push.SendNotificationToAdminsAsync(
                    $"Pago tanda: ${tanda.WeeklyAmount:F2}",
                    $"{clientName} pago la semana {req.WeekNumber} de {tanda.Name}.",
                    tag: "tanda-payment"
                );
            }

            return Ok(new
            {
                status = mpResult?.Status,
                statusDetail = mpResult?.StatusDetail,
                paymentId = mpResult?.Id
            });
        }
        catch (Exception)
        {
            return BadRequest(new { message = "No se pudo procesar el pago de la tanda." });
        }
    }

    public class TandaCardPaymentRequest
    {
        [Required]
        public Guid ParticipantId { get; set; }

        [Range(1, 500)]
        public int WeekNumber { get; set; }

        [Required]
        [MaxLength(2048)]
        public string CardToken { get; set; } = "";

        [Required]
        [MaxLength(64)]
        public string PaymentMethodId { get; set; } = "";
    }

    private string BuildTenantPaymentWebhookUrl(int businessId)
    {
        var configuredBaseUrl = _config["App:BackendUrl"]?.TrimEnd('/');
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}"
            : configuredBaseUrl;

        return $"{baseUrl}/api/payments/webhook?businessId={businessId}";
    }

    private static string ReadMercadoPagoError(string rawBody)
    {
        var mpErrorMsg = "Error al procesar el pago con Mercado Pago.";
        try
        {
            var errDoc = System.Text.Json.JsonDocument.Parse(rawBody);
            if (errDoc.RootElement.TryGetProperty("message", out var msgEl))
            {
                mpErrorMsg = msgEl.GetString() ?? mpErrorMsg;
            }

            if (errDoc.RootElement.TryGetProperty("cause", out var causeEl) && causeEl.GetArrayLength() > 0)
            {
                var desc = causeEl[0].TryGetProperty("description", out var d) ? d.GetString() : null;
                if (!string.IsNullOrEmpty(desc)) mpErrorMsg = desc;
            }
        }
        catch
        {
            // Mantener mensaje generico si Mercado Pago responde algo no JSON.
        }

        return mpErrorMsg;
    }

    private class MpPaymentApiResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("status_detail")]
        public string StatusDetail { get; set; } = "";
    }
}
