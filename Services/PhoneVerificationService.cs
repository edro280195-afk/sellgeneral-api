using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EntregasApi.Services;

public class WhatsAppOptions
{
    public string Provider { get; init; } = "MetaWhatsApp";
    public string DefaultCountryCode { get; init; } = "52";
    public int NationalNumberLength { get; init; } = 10;
    public string Channel { get; init; } = "whatsapp";

    public MetaWhatsAppOptions Meta { get; init; } = new();
    public CustomWhatsAppOptions Custom { get; init; } = new();

    // Compatibilidad previa con Twilio si existen referencias
    public TwilioVerifyOptions Twilio { get; init; } = new();
}

public sealed class SmsOptions : WhatsAppOptions { }

public sealed class MetaWhatsAppOptions
{
    public string PhoneNumberId { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public string TemplateName { get; init; } = "auth_otp";
    public string GraphApiVersion { get; init; } = "v20.0";
}

public sealed class CustomWhatsAppOptions
{
    public string ApiUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
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

/// <summary>
/// Servicio de verificación de teléfono directo por WhatsApp (Meta Cloud API o Custom WhatsApp Gateway).
/// Almacena y valida el código OTP localmente sin depender de Twilio Verify.
/// </summary>
public sealed class DirectWhatsAppVerificationService(
    HttpClient httpClient,
    IOptions<WhatsAppOptions> options,
    ILogger<DirectWhatsAppVerificationService> logger) : IPhoneVerificationService
{
    private readonly WhatsAppOptions _options = options.Value;
    private static readonly ConcurrentDictionary<string, (string Code, DateTime ExpiryUtc)> _otpCache = new();

    public bool IsConfigured =>
        (!string.IsNullOrWhiteSpace(_options.Meta.PhoneNumberId) && !string.IsNullOrWhiteSpace(_options.Meta.AccessToken)) ||
        !string.IsNullOrWhiteSpace(_options.Custom.ApiUrl);

    public string? NormalizePhone(string? input)
    {
        var digits = TextNormalizer.NormalizePhone(input);
        if (digits is null) return null;

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

    public async Task<PhoneVerificationOutcome> SendCodeAsync(
        string normalizedPhone,
        CancellationToken cancellationToken)
    {
        var code = RandomNumberGenerator.GetInt32(100000, 999999).ToString("D6");
        _otpCache[normalizedPhone] = (code, DateTime.UtcNow.AddMinutes(10));

        if (!IsConfigured)
        {
            logger.LogInformation("WhatsApp directo no configurado. Código OTP generado para {Phone}: {Code}", normalizedPhone, code);
            return PhoneVerificationOutcome.Sent;
        }

        if (!string.IsNullOrWhiteSpace(_options.Meta.PhoneNumberId) && !string.IsNullOrWhiteSpace(_options.Meta.AccessToken))
        {
            return await SendMetaWhatsAppAsync(normalizedPhone, code, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(_options.Custom.ApiUrl))
        {
            return await SendCustomWhatsAppAsync(normalizedPhone, code, cancellationToken);
        }

        return PhoneVerificationOutcome.Sent;
    }

    public Task<PhoneVerificationOutcome> CheckCodeAsync(
        string normalizedPhone,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Task.FromResult(PhoneVerificationOutcome.Invalid);
        }

        if (_otpCache.TryGetValue(normalizedPhone, out var entry))
        {
            if (DateTime.UtcNow <= entry.ExpiryUtc && string.Equals(entry.Code, code.Trim(), StringComparison.Ordinal))
            {
                _otpCache.TryRemove(normalizedPhone, out _);
                return Task.FromResult(PhoneVerificationOutcome.Approved);
            }
        }

        return Task.FromResult(PhoneVerificationOutcome.Invalid);
    }

    private async Task<PhoneVerificationOutcome> SendMetaWhatsAppAsync(
        string normalizedPhone,
        string code,
        CancellationToken cancellationToken)
    {
        var url = $"https://graph.facebook.com/{_options.Meta.GraphApiVersion}/{_options.Meta.PhoneNumberId}/messages";
        var payload = new
        {
            messaging_product = "whatsapp",
            to = ToE164(normalizedPhone),
            type = "template",
            template = new
            {
                name = _options.Meta.TemplateName,
                language = new { code = "es" },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        parameters = new[]
                        {
                            new { type = "text", text = code }
                        }
                    },
                    new
                    {
                        type = "button",
                        sub_type = "url",
                        index = "0",
                        parameters = new[]
                        {
                            new { type = "text", text = code }
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Meta.AccessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return PhoneVerificationOutcome.Sent;
            }

            logger.LogWarning("Meta WhatsApp Cloud API respondió con HTTP {StatusCode}", (int)response.StatusCode);
            return PhoneVerificationOutcome.ProviderUnavailable;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error al enviar mensaje por Meta WhatsApp Cloud API.");
            return PhoneVerificationOutcome.ProviderUnavailable;
        }
    }

    private async Task<PhoneVerificationOutcome> SendCustomWhatsAppAsync(
        string normalizedPhone,
        string code,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            phone = ToE164(normalizedPhone),
            code,
            message = $"Tu código de verificación de Neni's es: {code}"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Custom.ApiUrl);
        if (!string.IsNullOrWhiteSpace(_options.Custom.ApiKey))
        {
            request.Headers.Add("X-Api-Key", _options.Custom.ApiKey);
        }
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? PhoneVerificationOutcome.Sent : PhoneVerificationOutcome.ProviderUnavailable;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error al enviar mensaje por Custom WhatsApp API.");
            return PhoneVerificationOutcome.ProviderUnavailable;
        }
    }

    private string ToE164(string normalizedPhone)
    {
        return $"{DigitsOnly(_options.DefaultCountryCode)}{normalizedPhone}";
    }

    private static string DigitsOnly(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }
}
