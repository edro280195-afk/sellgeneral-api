using System.Net;
using System.Text;
using EntregasApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EntregasApi.Tests;

public class TwilioVerifyServiceTests
{
    [Theory]
    [InlineData("868 145 2290", "8681452290")]
    [InlineData("+52 868 145 2290", "8681452290")]
    [InlineData("0052 868 145 2290", "8681452290")]
    [InlineData("123", null)]
    public void NormalizePhone_UsesMexicanNationalFormat(
        string input,
        string? expected)
    {
        var service = BuildService(new StubHandler());

        Assert.Equal(expected, service.NormalizePhone(input));
    }

    [Fact]
    public async Task SendCodeAsync_PostsWhatsAppVerificationInSpanish()
    {
        var handler = new StubHandler(
            HttpStatusCode.Created,
            """{"status":"pending"}""");
        var service = BuildService(handler);

        var outcome = await service.SendCodeAsync("8681452290", default);

        Assert.Equal(PhoneVerificationOutcome.Sent, outcome);
        Assert.NotNull(handler.LastRequest);
        Assert.EndsWith(
            "/v2/Services/VA123/Verifications",
            handler.LastRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Basic", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Contains("To=%2B528681452290", handler.LastBody);
        Assert.Contains("Channel=whatsapp", handler.LastBody);
        Assert.Contains("Locale=es", handler.LastBody);
    }

    [Fact]
    public async Task SendCodeAsync_HonorsConfiguredChannel()
    {
        var handler = new StubHandler(
            HttpStatusCode.Created,
            """{"status":"pending"}""");
        var service = BuildService(handler, channel: "sms");

        await service.SendCodeAsync("8681452290", default);

        Assert.Contains("Channel=sms", handler.LastBody);
    }

    [Fact]
    public async Task CheckCodeAsync_Approved_ReturnsApproved()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"status":"approved"}""");
        var service = BuildService(handler);

        var outcome = await service.CheckCodeAsync(
            "8681452290",
            "123456",
            default);

        Assert.Equal(PhoneVerificationOutcome.Approved, outcome);
        Assert.Contains("Code=123456", handler.LastBody);
    }

    [Fact]
    public async Task CheckCodeAsync_NotFound_ReturnsInvalid()
    {
        var service = BuildService(new StubHandler(HttpStatusCode.NotFound, "{}"));

        var outcome = await service.CheckCodeAsync(
            "8681452290",
            "123456",
            default);

        Assert.Equal(PhoneVerificationOutcome.Invalid, outcome);
    }

    private static TwilioVerifyService BuildService(
        HttpMessageHandler handler,
        string channel = "whatsapp")
    {
        var options = Options.Create(new SmsOptions
        {
            Provider = "Twilio",
            DefaultCountryCode = "52",
            NationalNumberLength = 10,
            Channel = channel,
            Twilio = new TwilioVerifyOptions
            {
                AccountSid = "AC123",
                AuthToken = "secret",
                VerifyServiceSid = "VA123"
            }
        });
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://verify.twilio.com/")
        };

        return new TwilioVerifyService(
            client,
            options,
            NullLogger<TwilioVerifyService>.Instance);
    }

    private sealed class StubHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseBody = """{"status":"pending"}""") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
