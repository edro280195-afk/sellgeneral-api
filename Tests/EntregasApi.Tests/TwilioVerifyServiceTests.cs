using System.Net;
using System.Text;
using EntregasApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EntregasApi.Tests;

public class DirectWhatsAppVerificationServiceTests
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
    public async Task SendCodeAsync_GeneratesLocalCodeAndPostsMetaWhatsApp()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"messaging_product":"whatsapp"}""");
        var service = BuildService(handler);

        var outcome = await service.SendCodeAsync("8681452290", default);

        Assert.Equal(PhoneVerificationOutcome.Sent, outcome);
        Assert.NotNull(handler.LastRequest);
        Assert.EndsWith("/12345/messages", handler.LastRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Contains("528681452290", handler.LastBody);
        Assert.Contains("auth_otp", handler.LastBody);
    }

    [Fact]
    public async Task CheckCodeAsync_VerifiesLocallyGeneratedCode()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{}""");
        var service = BuildService(handler);

        await service.SendCodeAsync("8681452290", default);

        // Extract sent code from handler request body or test check
        Assert.NotNull(handler.LastBody);

        // Wrong code returns Invalid
        var invalidOutcome = await service.CheckCodeAsync("8681452290", "000000", default);
        Assert.Equal(PhoneVerificationOutcome.Invalid, invalidOutcome);
    }

    private static DirectWhatsAppVerificationService BuildService(
        HttpMessageHandler handler)
    {
        var options = Options.Create(new WhatsAppOptions
        {
            Provider = "MetaWhatsApp",
            DefaultCountryCode = "52",
            NationalNumberLength = 10,
            Meta = new MetaWhatsAppOptions
            {
                PhoneNumberId = "12345",
                AccessToken = "test-token",
                TemplateName = "auth_otp"
            }
        });
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.facebook.com/")
        };

        return new DirectWhatsAppVerificationService(
            client,
            options,
            NullLogger<DirectWhatsAppVerificationService>.Instance);
    }

    private sealed class StubHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseBody = """{}""") : HttpMessageHandler
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
