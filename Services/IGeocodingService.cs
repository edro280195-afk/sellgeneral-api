namespace EntregasApi.Services;

public record GeocodeResult(
    bool Success,
    double? Latitude,
    double? Longitude,
    string? FormattedAddress,
    string? Status,
    string? Error
);

public record AddressSuggestion(
    string PlaceId,
    string MainText,
    string SecondaryText,
    string Description
);

public record AddressDetails(
    string? FormattedAddress,
    double? Latitude,
    double? Longitude
);

public interface IGeocodingService
{
    Task<GeocodeResult> GeocodeAsync(string address, CancellationToken ct = default);

    Task<IReadOnlyList<AddressSuggestion>> AutocompleteAsync(
        string input,
        string? sessionToken = null,
        CancellationToken ct = default);

    Task<AddressDetails?> GetPlaceDetailsAsync(
        string placeId,
        string? sessionToken = null,
        CancellationToken ct = default);
}
