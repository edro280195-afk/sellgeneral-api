using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EntregasApi.Services;

public class GeocodingService : IGeocodingService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ICurrentBusiness _currentBusiness;
    private readonly ILogger<GeocodingService> _logger;

    public GeocodingService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ICurrentBusiness currentBusiness,
        ILogger<GeocodingService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _currentBusiness = currentBusiness;
        _logger = logger;
    }

    public async Task<GeocodeResult> GeocodeAsync(
        string address,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new GeocodeResult(false, null, null, null, "EMPTY_ADDRESS", "La direccion esta vacia");

        var apiKey = GetGeocodingApiKey();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "dummy")
            return new GeocodeResult(false, null, null, null, "NO_API_KEY", "API key no configurada");

        var region = (await _currentBusiness.GetAsync(ct)).GeocodingRegion;
        var biased = string.IsNullOrWhiteSpace(region) || address.Contains(region, StringComparison.OrdinalIgnoreCase)
            ? address
            : $"{address}, {region}";
        var url = $"https://maps.googleapis.com/maps/api/geocode/json?address={WebUtility.UrlEncode(biased)}&region=mx&language=es&key={apiKey}";

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new GeocodeResult(false, null, null, null, $"HTTP_{(int)response.StatusCode}", "Error HTTP");

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";
            if (status != "OK" || !doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return new GeocodeResult(false, null, null, null, status, $"Geocoder: {status}");

            var first = results[0];
            var location = first.GetProperty("geometry").GetProperty("location");
            var lat = location.GetProperty("lat").GetDouble();
            var lng = location.GetProperty("lng").GetDouble();
            var formatted = first.TryGetProperty("formatted_address", out var formattedElement)
                ? formattedElement.GetString()
                : null;
            return new GeocodeResult(true, lat, lng, formatted, status, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error geocodificando una direccion");
            return new GeocodeResult(false, null, null, null, "EXCEPTION", ex.Message);
        }
    }

    public async Task<IReadOnlyList<AddressSuggestion>> AutocompleteAsync(
        string input,
        string? sessionToken = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Trim().Length < 3)
            return Array.Empty<AddressSuggestion>();

        var apiKey = GetPlacesApiKey();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "dummy")
            return Array.Empty<AddressSuggestion>();

        try
        {
            var business = await _currentBusiness.GetAsync(ct);
            var requestBody = new Dictionary<string, object?>
            {
                ["input"] = input.Trim(),
                ["includedRegionCodes"] = new[] { "mx" },
                ["languageCode"] = "es",
                ["locationBias"] = new
                {
                    circle = new
                    {
                        center = new
                        {
                            latitude = business.DepotLat,
                            longitude = business.DepotLng
                        },
                        radius = 30000.0
                    }
                }
            };
            if (!string.IsNullOrWhiteSpace(sessionToken))
                requestBody["sessionToken"] = sessionToken.Trim();

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://places.googleapis.com/v1/places:autocomplete")
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Add("X-Goog-Api-Key", apiKey);
            request.Headers.Add(
                "X-Goog-FieldMask",
                "suggestions.placePrediction.placeId," +
                "suggestions.placePrediction.text," +
                "suggestions.placePrediction.structuredFormat");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Google Places autocomplete respondio {StatusCode}",
                    (int)response.StatusCode);
                return Array.Empty<AddressSuggestion>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("suggestions", out var suggestions))
                return Array.Empty<AddressSuggestion>();

            var result = new List<AddressSuggestion>();
            foreach (var suggestion in suggestions.EnumerateArray())
            {
                if (!suggestion.TryGetProperty("placePrediction", out var place))
                    continue;
                if (!place.TryGetProperty("placeId", out var idElement))
                    continue;

                var placeId = idElement.GetString();
                if (string.IsNullOrWhiteSpace(placeId)) continue;

                var description = ReadNestedText(place, "text") ?? string.Empty;
                var main = ReadNestedText(place, "structuredFormat", "mainText") ?? description;
                var secondary = ReadNestedText(place, "structuredFormat", "secondaryText") ?? string.Empty;
                result.Add(new AddressSuggestion(placeId, main, secondary, description));
            }

            return result.Take(6).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error consultando sugerencias de direccion");
            return Array.Empty<AddressSuggestion>();
        }
    }

    public async Task<AddressDetails?> GetPlaceDetailsAsync(
        string placeId,
        string? sessionToken = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(placeId)) return null;

        var apiKey = GetPlacesApiKey();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "dummy") return null;

        try
        {
            var resource = placeId.StartsWith("places/", StringComparison.Ordinal)
                ? placeId
                : $"places/{placeId}";
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            var url = $"https://places.googleapis.com/v1/{resource}";
            if (!string.IsNullOrWhiteSpace(sessionToken))
                url += $"?sessionToken={WebUtility.UrlEncode(sessionToken.Trim())}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Goog-Api-Key", apiKey);
            request.Headers.Add("X-Goog-FieldMask", "formattedAddress,location");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var formatted = root.TryGetProperty("formattedAddress", out var address)
                ? address.GetString()
                : null;
            if (!root.TryGetProperty("location", out var location))
                return new AddressDetails(formatted, null, null);

            var lat = location.TryGetProperty("latitude", out var latElement)
                ? latElement.GetDouble()
                : (double?)null;
            var lng = location.TryGetProperty("longitude", out var lngElement)
                ? lngElement.GetDouble()
                : (double?)null;
            return new AddressDetails(formatted, lat, lng);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error resolviendo un place id");
            return null;
        }
    }

    private string? GetGeocodingApiKey() => FirstConfigured(
        "Google:GeocodingApiKey",
        "Google:MapsApiKey",
        "Google:PlacesApiKey");

    private string? GetPlacesApiKey() => FirstConfigured(
        "Google:PlacesApiKey",
        "Google:MapsApiKey",
        "Google:GeocodingApiKey");

    private string? FirstConfigured(params string[] keys)
    {
        return keys
            .Select(key => _config[key])
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != "dummy");
    }

    private static string? ReadNestedText(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var property in path)
        {
            if (!current.TryGetProperty(property, out current)) return null;
        }
        return current.TryGetProperty("text", out var text)
            ? text.GetString()
            : current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
