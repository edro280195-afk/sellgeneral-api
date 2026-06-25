using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntregasApi.Models;
using Microsoft.Extensions.Options;

namespace EntregasApi.Services;

/// <summary>
/// Cliente HTTP de la API de preapproval de Mercado Pago, autenticado con
/// las credenciales de PLATAFORMA (no las del tenant). Es la pieza que el
/// webhook + los endpoints de suscripcion invocan para crear, ajustar y
/// cancelar el cobro recurrente de la mensualidad de cada vendedora.
/// </summary>
public sealed class MercadoPagoSubscriptionService : IMercadoPagoSubscriptionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MercadoPagoSubscriptionOptions _options;
    private readonly ILogger<MercadoPagoSubscriptionService> _logger;

    public MercadoPagoSubscriptionService(
        IHttpClientFactory httpClientFactory,
        IOptions<MercadoPagoSubscriptionOptions> options,
        ILogger<MercadoPagoSubscriptionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MercadoPagoPreapproval> CreatePreapprovalAsync(
        Business business,
        string planTier,
        SubscriptionPeriodicity periodicity,
        string payerEmail,
        string? cardTokenId,
        string externalReference,
        DateTime? firstChargeDate,
        CancellationToken cancellationToken = default)
    {
        var amount = PlanCatalog.GetPeriodAmount(planTier, periodicity);
        var body = new Dictionary<string, object?>
        {
            ["reason"] = $"Suscripcion {planTier} - {business.Name}",
            ["external_reference"] = externalReference,
            ["payer_email"] = payerEmail,
            ["auto_recurring"] = new Dictionary<string, object?>
            {
                ["frequency"] = (int)periodicity,
                ["frequency_type"] = "months",
                ["transaction_amount"] = amount,
                ["currency_id"] = _options.Currency
            },
            ["status"] = "authorized"
        };

        if (firstChargeDate is not null)
        {
            ((Dictionary<string, object?>)body["auto_recurring"]!)["start_date"] =
                firstChargeDate.Value.ToUniversalTime()
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(cardTokenId))
        {
            body["card_token_id"] = cardTokenId;
        }

        if (string.IsNullOrWhiteSpace(business.FrontendUrl) is false)
        {
            body["back_url"] = business.FrontendUrl;
        }

        var response = await SendAsync<MpPreapprovalResponse>(
            HttpMethod.Post,
            "preapproval",
            body,
            cancellationToken);

        return ToDomain(response);
    }

    public async Task<MercadoPagoPreapproval> UpdatePreapprovalAsync(
        string preapprovalId,
        string planTier,
        SubscriptionPeriodicity periodicity,
        CancellationToken cancellationToken = default)
    {
        var amount = PlanCatalog.GetPeriodAmount(planTier, periodicity);
        var body = new Dictionary<string, object?>
        {
            ["auto_recurring"] = new Dictionary<string, object?>
            {
                ["frequency"] = (int)periodicity,
                ["frequency_type"] = "months",
                ["transaction_amount"] = amount,
                ["currency_id"] = _options.Currency
            }
        };

        var response = await SendAsync<MpPreapprovalResponse>(
            HttpMethod.Put,
            $"preapproval/{Uri.EscapeDataString(preapprovalId)}",
            body,
            cancellationToken);

        return ToDomain(response);
    }

    public async Task<MercadoPagoPreapproval> CancelPreapprovalAsync(
        string preapprovalId,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["status"] = "cancelled"
        };

        var response = await SendAsync<MpPreapprovalResponse>(
            HttpMethod.Put,
            $"preapproval/{Uri.EscapeDataString(preapprovalId)}",
            body,
            cancellationToken);

        return ToDomain(response);
    }

    public async Task<MercadoPagoPreapproval?> GetPreapprovalAsync(
        string preapprovalId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync<MpPreapprovalResponse>(
                HttpMethod.Get,
                $"preapproval/{Uri.EscapeDataString(preapprovalId)}",
                body: null,
                cancellationToken);
            return ToDomain(response);
        }
        catch (MercadoPagoSubscriptionException ex) when (ex.Message.Contains("404"))
        {
            return null;
        }
    }

    public async Task<MercadoPagoAuthorizedPayment?> GetAuthorizedPaymentAsync(
        string authorizedPaymentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync<MpAuthorizedPaymentResponse>(
                HttpMethod.Get,
                $"authorized_payments/{Uri.EscapeDataString(authorizedPaymentId)}",
                body: null,
                cancellationToken);

            return new MercadoPagoAuthorizedPayment(
                response.Id,
                response.PreapprovalId,
                response.Status,
                response.StatusDetail,
                response.TransactionAmount,
                response.CurrencyId,
                response.DateCreated);
        }
        catch (MercadoPagoSubscriptionException ex) when (ex.Message.Contains("404"))
        {
            return null;
        }
    }

    public bool ValidateWebhookSignature(
        string? requestId,
        string? signatureHeader,
        string? dataId,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret) ||
            string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(signatureHeader) ||
            string.IsNullOrWhiteSpace(dataId))
        {
            return false;
        }

        // El header x-signature tiene la forma "ts=1234567890,v1=abcdef...".
        // Extraemos ts y v1, recomponemos el "manifest" segun la doc de MP
        // y comparamos HMAC-SHA256 con el secret de plataforma.
        var parts = signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries);
        string? tsValue = null;
        string? v1Value = null;

        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (string.Equals(key, "ts", StringComparison.OrdinalIgnoreCase))
            {
                tsValue = value;
            }
            else if (string.Equals(key, "v1", StringComparison.OrdinalIgnoreCase))
            {
                v1Value = value;
            }
        }

        if (tsValue is null || v1Value is null)
        {
            return false;
        }

        // Manifest = "id:<dataId>;request-id:<requestId>;ts:<ts>;"
        var manifest = $"id:{dataId};request-id:{requestId};ts:{tsValue};";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest));
        var expected = Convert.ToHexString(hash).ToLowerInvariant();

        var provided = v1Value.ToLowerInvariant();
        if (expected.Length != provided.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            throw new MercadoPagoSubscriptionException(
                "Platform:MercadoPago:AccessToken no esta configurado.");
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        using var request = new HttpRequestMessage(method, $"{_options.BaseUrl}/{path.TrimStart('/')}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MpPlatform] Error de red al llamar {Path}", path);
            throw new MercadoPagoSubscriptionException(
                "No se pudo conectar con Mercado Pago. Intenta de nuevo.", ex);
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "[MpPlatform] HTTP {StatusCode} en {Path}: {Body}",
                (int)response.StatusCode,
                path,
                raw);
            throw new MercadoPagoSubscriptionException(
                $"Mercado Pago respondio HTTP {(int)response.StatusCode}.");
        }

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(raw, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MpPlatform] Respuesta invalida de MP en {Path}: {Body}", path, raw);
            throw new MercadoPagoSubscriptionException(
                "Respuesta inesperada de Mercado Pago.", ex);
        }

        if (parsed is null)
        {
            throw new MercadoPagoSubscriptionException(
                "Mercado Pago devolvio una respuesta vacia.");
        }

        return parsed;
    }

    private static MercadoPagoPreapproval ToDomain(MpPreapprovalResponse response)
    {
        var auto = response.AutoRecurring;
        return new MercadoPagoPreapproval(
            response.Id,
            response.Status,
            response.PreapprovalPlanId,
            response.Reason,
            response.ExternalReference,
            auto?.TransactionAmount ?? 0m,
            auto?.CurrencyId ?? "MXN",
            auto?.Frequency ?? 1,
            auto?.FrequencyType ?? "months",
            response.NextPaymentDate,
            auto?.StartDate,
            auto?.EndDate,
            response.PayerEmail,
            response.PayerId,
            response.InitPoint);
    }
}
