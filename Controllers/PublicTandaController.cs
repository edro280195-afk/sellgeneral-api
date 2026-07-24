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

    /// <summary>
    /// GET /tanda-view/{token} - Atiende el enlace público directo de la tanda.
    /// Si existe wwwroot/index.html, sirve el SPA Angular.
    /// Si no existe archivo físico SPA, renderiza la vista pública enriquecida en HTML.
    /// </summary>
    [HttpGet("/tanda-view/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> ServeTandaWebPage(string token)
    {
        var indexPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "index.html");
        if (System.IO.File.Exists(indexPath))
        {
            return PhysicalFile(indexPath, "text/html");
        }

        var tanda = await _tandaService.GetTandaByTokenAsync(token);
        if (tanda == null)
        {
            return Content(@"<!DOCTYPE html>
<html lang=""es"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Tanda No Encontrada 💖</title>
    <script src=""https://cdn.tailwindcss.com""></script>
</head>
<body class=""bg-pink-50 min-h-screen flex items-center justify-center font-sans p-4"">
    <div class=""bg-white p-8 rounded-3xl shadow-xl max-w-sm text-center border border-pink-100"">
        <div class=""text-6xl mb-4"">🔍</div>
        <h2 class=""text-2xl font-black text-pink-900 mb-2"">Tanda no encontrada</h2>
        <p class=""text-pink-600 text-sm"">Verifica que el enlace sea correcto o solicita uno nuevo a tu vendedora. 🎀</p>
    </div>
</body>
</html>", "text/html");
        }

        var participantsHtml = string.Join("", tanda.Participants.Select(p => $@"
            <div class=""flex items-center justify-between p-3.5 rounded-2xl {(p.IsWinnerThisWeek ? "bg-pink-50 border-2 border-pink-300" : "bg-stone-50 border border-stone-100")}"">
                <div class=""flex items-center gap-3"">
                    <span class=""w-8 h-8 rounded-full {(p.IsWinnerThisWeek ? "bg-pink-600 text-white shadow-md shadow-pink-200" : "bg-pink-100 text-pink-700")} flex items-center justify-center text-xs font-black"">#{p.AssignedTurn}</span>
                    <div>
                        <p class=""text-sm font-black text-pink-950 leading-none mb-1"">{p.Name}</p>
                        {(p.WeeklyAmount.HasValue ? $"<p class=\"text-[10px] text-pink-500 font-bold\">${p.WeeklyAmount:N2} MXN / sem</p>" : "")}
                    </div>
                </div>
                <span class=""px-3 py-1 rounded-full text-xs font-black {(p.HasPaidCurrentWeek ? "bg-emerald-100 text-emerald-700" : "bg-amber-100 text-amber-700")}"">
                    {(p.HasPaidCurrentWeek ? "💖 Pagado" : "⏳ Pendiente")}
                </span>
            </div>"));

        return Content($@"<!DOCTYPE html>
<html lang=""es"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{tanda.Name} - Neni's App 🎀</title>
    <script src=""https://cdn.tailwindcss.com""></script>
    <link href=""https://fonts.googleapis.com/css2?family=Outfit:wght@400;600;800;900&display=swap"" rel=""stylesheet"">
    <style>body {{ font-family: 'Outfit', sans-serif; }}</style>
</head>
<body class=""bg-gradient-to-b from-pink-50 via-rose-50 to-purple-50 min-h-screen text-stone-800 p-4 sm:p-6"">
    <div class=""max-w-md mx-auto pt-6 space-y-6"">
        <!-- Hero Header -->
        <div class=""text-center"">
            <div class=""text-5xl mb-2"">🎀</div>
            <h1 class=""text-3xl font-black text-pink-600 tracking-tight mb-1"">{tanda.Name}</h1>
            <p class=""text-rose-500 font-medium text-sm"">¡Creciendo juntas en grupo! ✨</p>
        </div>

        <!-- Banner App Download -->
        <div class=""bg-gradient-to-br from-pink-500 via-rose-500 to-purple-600 p-6 text-white shadow-2xl rounded-[2.5rem] relative overflow-hidden border-2 border-white/40"">
            <div class=""relative z-10 space-y-3"">
                <div class=""inline-flex items-center gap-1.5 bg-white/20 backdrop-blur-md px-3 py-1 rounded-full text-[9px] font-black uppercase tracking-widest text-pink-100"">
                    ✨ Neni's App Clientas
                </div>
                <h3 class=""text-xl font-black leading-tight"">¡Lleva tus Tandas y Compras en tu celular! 📲</h3>
                <p class=""text-xs text-pink-100 font-medium leading-relaxed"">Descarga la app oficial para recibir notificaciones de tu turno en tiempo real y abonar en 1 toque.</p>
                <div class=""flex flex-wrap gap-2 pt-1"">
                    <a href=""https://nenisapp.com/download"" target=""_blank"" class=""flex items-center gap-2 bg-white text-pink-900 font-black text-xs px-4 py-2.5 rounded-2xl shadow-lg"">🍎 App Store</a>
                    <a href=""https://nenisapp.com/download"" target=""_blank"" class=""flex items-center gap-2 bg-white/20 border border-white/40 text-white font-black text-xs px-4 py-2.5 rounded-2xl backdrop-blur-md"">🤖 Google Play</a>
                </div>
            </div>
        </div>

        <!-- Tanda Details Card -->
        <div class=""bg-white/90 rounded-[2.5rem] p-6 shadow-xl border border-pink-100 text-center space-y-3"">
            <p class=""text-[10px] font-black text-pink-400 uppercase tracking-widest"">Estado de la Tanda</p>
            <div class=""text-4xl font-black text-pink-950 font-display"">Semana {tanda.CurrentWeek} de {tanda.TotalWeeks}</div>
            <div class=""bg-pink-100/60 px-4 py-2 rounded-2xl inline-block text-xs font-black text-pink-700 border border-pink-200"">
                💰 Abono Semanal: ${tanda.WeeklyAmount:N2} MXN
            </div>
        </div>

        <!-- Participants List -->
        <div class=""bg-white/90 rounded-[2.5rem] p-6 shadow-xl border border-pink-100 space-y-3"">
            <h3 class=""text-xs font-black text-pink-400 uppercase tracking-widest mb-3"">Turnos del Grupo</h3>
            <div class=""space-y-2"">
                {participantsHtml}
            </div>
        </div>
    </div>
</body>
</html>", "text/html");
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
            .IgnoreQueryFilters()
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
