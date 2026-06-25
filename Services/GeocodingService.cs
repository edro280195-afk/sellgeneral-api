using System.Net;
using System.Text.Json;

namespace EntregasApi.Services;

public class GeocodingService : IGeocodingService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ICurrentBusiness _currentBusiness;
    private readonly ILogger<GeocodingService> _logger;

    public GeocodingService(IHttpClientFactory httpFactory, IConfiguration config, ICurrentBusiness currentBusiness, ILogger<GeocodingService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _currentBusiness = currentBusiness;
        _logger = logger;
    }

    public async Task<GeocodeResult> GeocodeAsync(string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new GeocodeResult(false, null, null, null, "EMPTY_ADDRESS", "La dirección está vacía");

        var apiKey = _config["Google:GeocodingApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "dummy")
            return new GeocodeResult(false, null, null, null, "NO_API_KEY", "API key no configurada");

        // Bias hacia la región del negocio activo (antes fijo a "Nuevo Laredo").
        var region = (await _currentBusiness.GetAsync(ct)).GeocodingRegion;
        var biased = string.IsNullOrWhiteSpace(region) || address.Contains(region, StringComparison.OrdinalIgnoreCase)
            ? address
            : $"{address}, {region}";

        var url = $"https://maps.googleapis.com/maps/api/geocode/json?address={WebUtility.UrlEncode(biased)}&region=mx&language=es&key={apiKey}";

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new GeocodeResult(false, null, null, null, "HTTP_" + (int)resp.StatusCode, "Error HTTP");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";

            if (status != "OK" || !doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return new GeocodeResult(false, null, null, null, status, $"Geocoder: {status}");

            var first = results[0];
            var loc = first.GetProperty("geometry").GetProperty("location");
            var lat = loc.GetProperty("lat").GetDouble();
            var lng = loc.GetProperty("lng").GetDouble();
            var formatted = first.TryGetProperty("formatted_address", out var fa) ? fa.GetString() : null;

            return new GeocodeResult(true, lat, lng, formatted, status, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error geocodificando: {Address}", address);
            return new GeocodeResult(false, null, null, null, "EXCEPTION", ex.Message);
        }
    }
}
