namespace EntregasApi.Services;

public static class RouteStopOrderResolver
{
    public static List<string> Resolve(
        IReadOnlyCollection<RouteStop> validStops,
        IEnumerable<string> preferredStopIds)
    {
        var validById = validStops.ToDictionary(
            stop => stop.Id,
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>(validStops.Count);

        foreach (var requestedId in preferredStopIds)
        {
            var candidate = requestedId?.Trim();
            if (string.IsNullOrEmpty(candidate)
                || !validById.TryGetValue(candidate, out var validStop)
                || !seen.Add(validStop.Id))
            {
                continue;
            }

            resolved.Add(validStop.Id);
        }

        // Una previsualización vieja o parcial no debe dejar entregas válidas
        // fuera de la ruta. Se agregan al final en el orden estable del request.
        foreach (var stop in validStops)
        {
            if (seen.Add(stop.Id))
            {
                resolved.Add(stop.Id);
            }
        }

        return resolved;
    }
}
