using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EntregasApi.Services;

public sealed class SmsOptions
{
    public string Provider { get; init; } = "Twilio";
    public string DefaultCountryCode { get; init; } = "52";
    public int NationalNumberLength { get; init; } = 10;

    /// <summary>
    /// Canal de Twilio Verify para enviar el código: "whatsapp" (default), "sms" o "call".
    /// Para WhatsApp, el Verify Service de Twilio debe tener un sender de WhatsApp aprobado.
    /// </summary>
    public string Channel { get; init; } = "whatsapp";

    public TwilioVerifyOptions Twilio { get; init; } = new();
}

public sealed class TwilioVerifyOptions
{
    public string AccountSid { get; init; } = string.Empty;
    public string AuthToken { get; init; } = string.Empty;
    public string VerifyServiceSid { get; init; } = string.Empty;
}

public enum PhoneVerificationOutcome
{
    Sent,
    Approved,
    Invalid,
    ProviderUnavailable
}

public interface IPhoneVerificationService
{
    bool IsConfigured { get; }
    string? NormalizePhone(string? input);
    Task<PhoneVerificationOutcome> SendCodeAsync(
        string normalizedPhone,
        CancellationToken cancellationToken);
    Task<PhoneVerificationOutcome> CheckCodeAsync(
        string normalizedPhone,
        string code,
        CancellationToken cancellationToken);
}

public sealed class TwilioVerifyService(
    HttpClient httpClient,
    IOptions<SmsOptions> options,
    ILogger<TwilioVerifyService> logger) : IPhoneVerificationService
{
    private readonly SmsOptions _options = options.Value;

    public bool IsConfigured =>
        string.Equals(_options.Provider, "Twilio", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_options.Twilio.AccountSid) &&
        !string.IsNullOrWhiteSpace(_options.Twilio.AuthToken) &&
        !string.IsNullOrWhiteSpace(_options.Twilio.VerifyServiceSid);

    public string? NormalizePhone(string? input)
    {
        var digits = TextNormalizer.NormalizePhone(input);
        if (digits is null)
        {
            return null;
        }

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        var countryCode = DigitsOnly(_options.DefaultCountryCode);
        if (digits.Length == _options.NationalNumberLength)
        {
            return digits;
        }

        if (countryCode.Length > 0 &&
            digits.Length == countryCode.Length + _options.NationalNumberLength &&
            digits.StartsWith(countryCode, StringComparison.Ordinal))
        {
            return digits[countryCode.Length..];
        }

        return null;
    }

    public Task<PhoneVerificationOutcome> SendCodeAsync(
        string normalizedPhone,
        CancellationToken cancellationToken)
    {
        var channel = string.IsNullOrWhiteSpace(_options.Channel)
            ? "whatsapp"
            : _options.Channel.Trim().ToLowerInvariant();

        return PostAsync(
            "Verifications",
            [
                new KeyValuePair<string, string>("To", ToE164(normalizedPhone)),
                new KeyValuePair<string, string>("Channel", channel),
                new KeyValuePair<string, string>("Locale", "es")
            ],
            isVerificationCheck: false,
            cancellationToken);
    }

    public Task<PhoneVerificationOutcome> CheckCodeAsync(
        string normalizedPhone,
        string code,
        CancellationToken cancellationToken)
    {
        return PostAsync(
            "VerificationCheck",
            [
                new KeyValuePair<string, string>("To", ToE164(normalizedPhone)),
                new KeyValuePair<string, string>("Code", code)
            ],
            isVerificationCheck: true,
            cancellationToken);
    }

    private async Task<PhoneVerificationOutcome> PostAsync(
        string resource,
        IReadOnlyCollection<KeyValuePair<string, string>> form,
        bool isVerificationCheck,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return PhoneVerificationOutcome.ProviderUnavailable;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"v2/Services/{Uri.EscapeDataString(_options.Twilio.VerifyServiceSid)}/{resource}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes(
                $"{_options.Twilio.AccountSid}:{_options.Twilio.AuthToken}")));
        request.Content = new FormUrlEncodedContent(form);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (isVerificationCheck &&
                    response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    return PhoneVerificationOutcome.Invalid;
                }

                logger.LogWarning(
                    "Twilio Verify rechazo {Operation} con HTTP {StatusCode}.",
                    isVerificationCheck ? "la validacion" : "el envio",
                    (int)response.StatusCode);
                return PhoneVerificationOutcome.ProviderUnavailable;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<TwilioVerifyResponse>(
                stream,
                cancellationToken: cancellationToken);
            var status = payload?.Status;

            if (isVerificationCheck)
            {
                return string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase)
                    ? PhoneVerificationOutcome.Approved
                    : PhoneVerificationOutcome.Invalid;
            }

            return string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)
                ? PhoneVerificationOutcome.Sent
                : PhoneVerificationOutcome.ProviderUnavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Twilio Verify excedio el tiempo de espera.");
            return PhoneVerificationOutcome.ProviderUnavailable;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "No se pudo conectar con Twilio Verify.");
            return PhoneVerificationOutcome.ProviderUnavailable;
        }
    }

    private string ToE164(string normalizedPhone)
    {
        return $"+{DigitsOnly(_options.DefaultCountryCode)}{normalizedPhone}";
    }

    private static string DigitsOnly(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private sealed record TwilioVerifyResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("status")]
        string? Status);
}
