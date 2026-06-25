using System.Text.Json.Serialization;
using EntregasApi.Models;

namespace EntregasApi.Services;

// ── Mercado Pago (plataforma) — Suscripciones (Fase 1.3) ──

/// <summary>
/// Resultado normalizado de un preapproval de MP. Lo consume el backend
/// para mapear a estado interno de la suscripcion.
/// </summary>
public sealed record MercadoPagoPreapproval(
    string Id,
    string Status,
    string? PreapprovalPlanId,
    string? Reason,
    string? ExternalReference,
    decimal TransactionAmount,
    string CurrencyId,
    int Frequency,
    string FrequencyType,
    DateTime? NextPaymentDate,
    DateTime? StartDate,
    DateTime? EndDate,
    string? PayerEmail,
    long? PayerId,
    string? InitPoint);

public sealed record MercadoPagoAuthorizedPayment(
    string Id,
    string? PreapprovalId,
    string Status,
    string? StatusDetail,
    decimal TransactionAmount,
    string CurrencyId,
    DateTime? DateCreated);

public interface IMercadoPagoSubscriptionService
{
    /// <summary>
    /// Crea un preapproval en MP usando credenciales de PLATAFORMA. La
    /// suscripcion arranca al final del trial (TrialEndsAt) para que el
    /// primer cobro se haga al CONVERTIR.
    /// </summary>
    Task<MercadoPagoPreapproval> CreatePreapprovalAsync(
        Business business,
        string planTier,
        SubscriptionPeriodicity periodicity,
        string payerEmail,
        string? cardTokenId,
        string externalReference,
        DateTime? firstChargeDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Actualiza el monto a cobrar y la periodicidad de un preapproval
    /// existente. Usado para upgrade inmediato.
    /// </summary>
    Task<MercadoPagoPreapproval> UpdatePreapprovalAsync(
        string preapprovalId,
        string planTier,
        SubscriptionPeriodicity periodicity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancela un preapproval en MP. La suscripcion sigue activa hasta
    /// CurrentPeriodEndsAt; en MP queda con status "cancelled".
    /// </summary>
    Task<MercadoPagoPreapproval> CancelPreapprovalAsync(
        string preapprovalId,
        CancellationToken cancellationToken = default);

    /// <summary>Consulta el estado real del preapproval en MP.</summary>
    Task<MercadoPagoPreapproval?> GetPreapprovalAsync(
        string preapprovalId,
        CancellationToken cancellationToken = default);

    /// <summary>Consulta una factura autorizada (cobro recurrente) en MP.</summary>
    Task<MercadoPagoAuthorizedPayment?> GetAuthorizedPaymentAsync(
        string authorizedPaymentId,
        CancellationToken cancellationToken = default);

    /// <summary>Valida la firma x-signature de un webhook de MP.</summary>
    bool ValidateWebhookSignature(
        string? requestId,
        string? signatureHeader,
        string? dataId,
        DateTimeOffset now);
}

public sealed class MercadoPagoSubscriptionOptions
{
    public string AccessToken { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.mercadopago.com";
    public string Currency { get; set; } = "MXN";
}

public sealed class MercadoPagoSubscriptionException : Exception
{
    public MercadoPagoSubscriptionException(string message) : base(message) { }
    public MercadoPagoSubscriptionException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class MpPreapprovalResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("preapproval_plan_id")]
    public string? PreapprovalPlanId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("external_reference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("auto_recurring")]
    public MpAutoRecurring? AutoRecurring { get; set; }

    [JsonPropertyName("init_point")]
    public string? InitPoint { get; set; }

    [JsonPropertyName("payer_email")]
    public string? PayerEmail { get; set; }

    [JsonPropertyName("payer_id")]
    public long? PayerId { get; set; }

    [JsonPropertyName("next_payment_date")]
    public DateTime? NextPaymentDate { get; set; }

    [JsonPropertyName("date_created")]
    public DateTime? DateCreated { get; set; }
}

internal sealed class MpAutoRecurring
{
    [JsonPropertyName("frequency")]
    public int Frequency { get; set; }

    [JsonPropertyName("frequency_type")]
    public string FrequencyType { get; set; } = "months";

    [JsonPropertyName("transaction_amount")]
    public decimal TransactionAmount { get; set; }

    [JsonPropertyName("currency_id")]
    public string CurrencyId { get; set; } = "MXN";

    [JsonPropertyName("start_date")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("end_date")]
    public DateTime? EndDate { get; set; }
}

internal sealed class MpAuthorizedPaymentResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("preapproval_id")]
    public string? PreapprovalId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("status_detail")]
    public string? StatusDetail { get; set; }

    [JsonPropertyName("transaction_amount")]
    public decimal TransactionAmount { get; set; }

    [JsonPropertyName("currency_id")]
    public string CurrencyId { get; set; } = "MXN";

    [JsonPropertyName("date_created")]
    public DateTime? DateCreated { get; set; }
}
