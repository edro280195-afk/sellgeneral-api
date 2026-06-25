using System.Net;
using System.Net.Http;
using System.Text.Json;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EntregasApi.Tests;

public class MercadoPagoSubscriptionServiceTests
{
    private static MercadoPagoSubscriptionOptions DefaultOptions() => new()
    {
        AccessToken = "TEST-ACCESS",
        PublicKey = "TEST-PUB",
        WebhookSecret = "test-secret",
        BaseUrl = "https://api.mercadopago.test",
        Currency = "MXN"
    };

    private static (MercadoPagoSubscriptionService svc, RecordingHandler handler) Build(
        Action<RecordingHandler>? configure = null)
    {
        var handler = new RecordingHandler();
        configure?.Invoke(handler);
        var factory = new SimpleHttpClientFactory(handler);
        var svc = new MercadoPagoSubscriptionService(
            factory,
            Options.Create(DefaultOptions()),
            NullLogger<MercadoPagoSubscriptionService>.Instance);
        return (svc, handler);
    }

    [Fact]
    public async Task CreatePreapproval_PostsToMp_WithAutoRecurring()
    {
        var (svc, handler) = Build();
        var business = new Business
        {
            Id = 7,
            Name = "Tienda X",
            Slug = "tienda-x",
            FrontendUrl = "https://tienda.example.com"
        };
        handler.Enqueue(Ok("""
        {
          "id": "PRE-1",
          "status": "authorized",
          "payer_email": "owner@x.com",
          "external_reference": "business_7",
          "auto_recurring": {
            "frequency": 1,
            "frequency_type": "months",
            "transaction_amount": 250,
            "currency_id": "MXN",
            "start_date": "2026-07-25T00:00:00.000Z"
          },
          "next_payment_date": "2026-07-25T00:00:00.000Z"
        }
        """));

        var pre = await svc.CreatePreapprovalAsync(
            business,
            PlanTiers.Pro,
            SubscriptionPeriodicity.Monthly,
            "owner@x.com",
            cardTokenId: "tok-abc",
            externalReference: "business_7",
            firstChargeDate: new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("PRE-1", pre.Id);
        Assert.Equal("authorized", pre.Status);
        Assert.Equal(250m, pre.TransactionAmount);
        Assert.Equal("MXN", pre.CurrencyId);
        Assert.Equal(1, pre.Frequency);
        Assert.Equal("months", pre.FrequencyType);

        var request = handler.Sent.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/preapproval", request.Path);

        using var doc = JsonDocument.Parse(request.BodyJson!);
        var root = doc.RootElement;
        Assert.Equal("authorized", root.GetProperty("status").GetString());
        Assert.Equal("owner@x.com", root.GetProperty("payer_email").GetString());
        Assert.Equal("tok-abc", root.GetProperty("card_token_id").GetString());
        Assert.Equal("https://tienda.example.com", root.GetProperty("back_url").GetString());

        var auto = root.GetProperty("auto_recurring");
        Assert.Equal(1, auto.GetProperty("frequency").GetInt32());
        Assert.Equal("months", auto.GetProperty("frequency_type").GetString());
        Assert.Equal(250m, auto.GetProperty("transaction_amount").GetDecimal());
        Assert.Equal("MXN", auto.GetProperty("currency_id").GetString());
        Assert.StartsWith("2026-07-25", auto.GetProperty("start_date").GetString());
    }

    [Fact]
    public async Task UpdatePreapproval_PutsNewAmount()
    {
        var (svc, handler) = Build();
        handler.Enqueue(Ok("""
        { "id": "PRE-1", "status": "authorized",
          "auto_recurring": { "frequency": 12, "frequency_type": "months", "transaction_amount": 4416, "currency_id": "MXN" } }
        """));

        var pre = await svc.UpdatePreapprovalAsync(
            "PRE-1",
            PlanTiers.Elite,
            SubscriptionPeriodicity.Annual);

        Assert.Equal("PRE-1", pre.Id);
        Assert.Equal(4416m, pre.TransactionAmount);
        Assert.Equal(12, pre.Frequency);

        var request = handler.Sent.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/preapproval/PRE-1", request.Path);

        using var doc = JsonDocument.Parse(request.BodyJson!);
        var auto = doc.RootElement.GetProperty("auto_recurring");
        Assert.Equal(4416m, auto.GetProperty("transaction_amount").GetDecimal());
        Assert.Equal(12, auto.GetProperty("frequency").GetInt32());
    }

    [Fact]
    public async Task CancelPreapproval_PutsCancelledStatus()
    {
        var (svc, handler) = Build();
        handler.Enqueue(Ok("""
        { "id": "PRE-1", "status": "cancelled",
          "auto_recurring": { "frequency": 1, "frequency_type": "months", "transaction_amount": 250, "currency_id": "MXN" } }
        """));

        var pre = await svc.CancelPreapprovalAsync("PRE-1");
        Assert.Equal("cancelled", pre.Status);

        var request = handler.Sent.Single();
        using var doc = JsonDocument.Parse(request.BodyJson!);
        Assert.Equal("cancelled", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetPreapproval_404_ReturnsNull()
    {
        var (svc, handler) = Build();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });

        var pre = await svc.GetPreapprovalAsync("missing");
        Assert.Null(pre);
    }

    [Fact]
    public async Task MissingAccessToken_ThrowsCleanException()
    {
        var handler = new RecordingHandler();
        var svc = new MercadoPagoSubscriptionService(
            new SimpleHttpClientFactory(handler),
            Options.Create(new MercadoPagoSubscriptionOptions
            {
                AccessToken = "",
                Currency = "MXN"
            }),
            NullLogger<MercadoPagoSubscriptionService>.Instance);

        await Assert.ThrowsAsync<MercadoPagoSubscriptionException>(() =>
            svc.GetPreapprovalAsync("PRE-1"));
    }

    [Fact]
    public void ValidateWebhookSignature_AcceptsValidHmac()
    {
        var (svc, _) = Build();
        // Reproducimos el calculo de la firma
        var manifest = "id:RES-1;request-id:REQ-1;ts:1700000000;";
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes("test-secret"));
        var hash = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(manifest)))
            .ToLowerInvariant();
        var header = $"ts=1700000000,v1={hash}";

        var ok = svc.ValidateWebhookSignature("REQ-1", header, "RES-1", DateTimeOffset.UtcNow);
        Assert.True(ok);
    }

    [Fact]
    public void ValidateWebhookSignature_RejectsWrongSecret()
    {
        var (svc, _) = Build();
        var header = "ts=1700000000,v1=deadbeef";
        var ok = svc.ValidateWebhookSignature("REQ-1", header, "RES-1", DateTimeOffset.UtcNow);
        Assert.False(ok);
    }

    [Fact]
    public void ValidateWebhookSignature_RejectsMissingFields()
    {
        var (svc, _) = Build();
        Assert.False(svc.ValidateWebhookSignature(null, "ts=1,v1=abc", "RES-1", DateTimeOffset.UtcNow));
        Assert.False(svc.ValidateWebhookSignature("REQ-1", null, "RES-1", DateTimeOffset.UtcNow));
        Assert.False(svc.ValidateWebhookSignature("REQ-1", "ts=1,v1=abc", null, DateTimeOffset.UtcNow));
        Assert.False(svc.ValidateWebhookSignature("REQ-1", "garbage", "RES-1", DateTimeOffset.UtcNow));
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public List<RecordedRequest> Sent { get; } = new();

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var bodyJson = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!.AbsolutePath,
            bodyJson,
            request.Headers.Authorization?.Parameter));
        return _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, string Path, string? BodyJson, string? Auth);

internal sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public SimpleHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
