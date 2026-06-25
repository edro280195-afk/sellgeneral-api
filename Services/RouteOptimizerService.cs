using EntregasApi.Models;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace EntregasApi.Services;

public class RouteOptimizerService : IRouteOptimizerService
{
    private const int MaxGoogleStops = 26; // origen + destino + hasta 25 intermedios en Google Routes.

    private readonly IEntitlementService _entitlements;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RouteOptimizerService> _logger;

    public RouteOptimizerService(
        IEntitlementService entitlements,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<RouteOptimizerService> logger)
    {
        _entitlements = entitlements;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<OptimizedRoute> OptimizeAsync(
        List<RouteStop> stops,
        double startLat,
        double startLng,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (stops == null || stops.Count == 0)
            return new OptimizedRoute(new List<string>(), 0, 0, "empty");

        if (!await _entitlements.HasFeatureAsync(Feature.TrafficRouteOptimization, ct))
        {
            return OptimizeWithHeuristic(stops, startLat, startLng, "haversine+2opt");
        }

        try
        {
            await _entitlements.EnsureWithinLimitAsync(LimitKey.RouteOptimizationCalls, currentCount: 0, ct);
            var googleResult = await TryOptimizeWithGoogleRoutesAsync(stops, startLat, startLng, ct);
            if (googleResult is not null)
            {
                return googleResult;
            }
        }
        catch (Exception ex) when (ex is not EntitlementLimitExceededException)
        {
            _logger.LogWarning(ex, "Google Routes no pudo optimizar la ruta. Se usara heuristica local.");
        }

        return OptimizeWithHeuristic(stops, startLat, startLng, "elite-haversine+2opt");
    }

    private OptimizedRoute OptimizeWithHeuristic(
        List<RouteStop> stops,
        double startLat,
        double startLng,
        string optimizedSource)
    {
        var withCoords = stops.Where(s => s.Latitude.HasValue && s.Longitude.HasValue).ToList();
        var withoutCoords = stops.Where(s => !s.Latitude.HasValue || !s.Longitude.HasValue).ToList();

        if (withCoords.Count == 0)
            return new OptimizedRoute(stops.Select(s => s.Id).ToList(), 0, 0, "no-coords");

        if (withCoords.Count == 1)
        {
            var single = withCoords.Concat(withoutCoords).Select(s => s.Id).ToList();
            return new OptimizedRoute(single, 0, 0, "single-stop");
        }

        int n = withCoords.Count;

        // 1) Construir matriz local sin Google.
        var hav = BuildHaversineMatrix(withCoords, startLat, startLng);
        var durMatrix = hav.dur;
        var distMatrix = hav.dist;

        // ── 2) Resolver TSP de ruta ABIERTA: inicio fijo en depot (nodo 0), final libre ──
        // Costo = tiempo de viaje (segundos). 2-opt elimina los cruces del nearest-neighbor greedy.
        var orderIdx = SolveOpenRoute(durMatrix, n); // índices de nodo 1..n en orden

        var orderedStops = orderIdx.Select(i => withCoords[i - 1]).ToList();

        // Totales a partir de la matriz (depot -> primera -> ... -> última, sin regreso).
        double totalDist = 0, totalDur = 0;
        int prev = 0;
        foreach (var idx in orderIdx)
        {
            totalDist += distMatrix[prev][idx];
            totalDur += durMatrix[prev][idx];
            prev = idx;
        }
        int distanceMeters = (int)Math.Round(totalDist);
        int durationSeconds = (int)Math.Round(totalDur);

        var allIds = orderedStops.Select(s => s.Id)
            .Concat(withoutCoords.Select(s => s.Id))
            .ToList();

        return new OptimizedRoute(allIds, distanceMeters, durationSeconds, optimizedSource);
    }

    private async Task<OptimizedRoute?> TryOptimizeWithGoogleRoutesAsync(
        List<RouteStop> stops,
        double startLat,
        double startLng,
        CancellationToken ct)
    {
        var apiKey = _config["Google:RoutesApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "dummy")
        {
            return null;
        }

        var withCoords = stops.Where(s => s.Latitude.HasValue && s.Longitude.HasValue).ToList();
        var withoutCoords = stops.Where(s => !s.Latitude.HasValue || !s.Longitude.HasValue).ToList();

        if (withCoords.Count < 2 || withCoords.Count > MaxGoogleStops)
        {
            return null;
        }

        var destination = withCoords
            .OrderByDescending(s => HaversineKm(startLat, startLng, s.Latitude!.Value, s.Longitude!.Value))
            .First();
        var intermediates = withCoords.Where(s => s.Id != destination.Id).ToList();

        var requestBody = new
        {
            origin = ToWaypoint(startLat, startLng),
            destination = ToWaypoint(destination),
            intermediates = intermediates.Select(ToWaypoint).ToList(),
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE",
            optimizeWaypointOrder = true,
            polylineQuality = "OVERVIEW"
        };

        var http = _httpFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://routes.googleapis.com/directions/v2:computeRoutes")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add(
            "X-Goog-FieldMask",
            "routes.distanceMeters,routes.duration,routes.polyline.encodedPolyline,routes.optimizedIntermediateWaypointIndex");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Google Routes respondio {StatusCode}: {Body}",
                (int)response.StatusCode,
                body);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
        {
            return null;
        }

        var route = routes[0];
        var orderedStops = new List<RouteStop>();
        if (route.TryGetProperty("optimizedIntermediateWaypointIndex", out var optimizedIndexes))
        {
            foreach (var indexElement in optimizedIndexes.EnumerateArray())
            {
                var index = indexElement.GetInt32();
                if (index >= 0 && index < intermediates.Count)
                {
                    orderedStops.Add(intermediates[index]);
                }
            }
        }
        else
        {
            orderedStops.AddRange(intermediates);
        }

        orderedStops.Add(destination);
        orderedStops.AddRange(withoutCoords);

        var distanceMeters = route.TryGetProperty("distanceMeters", out var distanceElement)
            ? distanceElement.GetInt32()
            : 0;
        var durationSeconds = route.TryGetProperty("duration", out var durationElement)
            ? ParseGoogleDurationSeconds(durationElement.GetString())
            : 0;
        var polyline = route.TryGetProperty("polyline", out var polylineElement)
                       && polylineElement.TryGetProperty("encodedPolyline", out var encodedPolyline)
            ? encodedPolyline.GetString()
            : null;

        return new OptimizedRoute(
            orderedStops.Select(s => s.Id).ToList(),
            distanceMeters,
            durationSeconds,
            "google-routes",
            polyline);
    }

    private static object ToWaypoint(RouteStop stop) => ToWaypoint(stop.Latitude!.Value, stop.Longitude!.Value);

    private static object ToWaypoint(double latitude, double longitude)
    {
        return new
        {
            location = new
            {
                latLng = new
                {
                    latitude,
                    longitude
                }
            }
        };
    }

    private static int ParseGoogleDurationSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.EndsWith("s", StringComparison.Ordinal))
        {
            return 0;
        }

        return double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? (int)Math.Round(seconds)
            : 0;
    }

    private static (double[][] dur, double[][] dist) BuildHaversineMatrix(
        List<RouteStop> stops, double depotLat, double depotLng)
    {
        int N = stops.Count + 1;
        var pts = new List<(double lat, double lng)>(N) { (depotLat, depotLng) };
        foreach (var s in stops) pts.Add((s.Latitude!.Value, s.Longitude!.Value));

        var dur = new double[N][];
        var dist = new double[N][];
        for (int i = 0; i < N; i++) { dur[i] = new double[N]; dist[i] = new double[N]; }

        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                if (i == j) continue;
                double km = HaversineKm(pts[i].lat, pts[i].lng, pts[j].lat, pts[j].lng);
                dist[i][j] = km * 1000.0;
                dur[i][j] = km / 30.0 * 3600.0;
            }

        return (dur, dist);
    }

    // ───────────────────────────────────────────────────────────────────
    //  TSP de ruta abierta: nearest-neighbor (semilla) + mejora 2-opt.
    //  cost[0] = depot fijo al inicio; sin regreso (final libre).
    // ───────────────────────────────────────────────────────────────────
    private static List<int> SolveOpenRoute(double[][] cost, int n)
    {
        var path = new List<int>(n);
        var visited = new bool[n + 1];
        visited[0] = true;
        int current = 0;

        for (int step = 0; step < n; step++)
        {
            int best = -1;
            double bestC = double.MaxValue;
            for (int j = 1; j <= n; j++)
            {
                if (visited[j]) continue;
                if (cost[current][j] < bestC) { bestC = cost[current][j]; best = j; }
            }
            path.Add(best);
            visited[best] = true;
            current = best;
        }

        TwoOptOpen(path, cost);
        return path;
    }

    private static void TwoOptOpen(List<int> path, double[][] cost)
    {
        // full = [depot(0), path...]; ruta abierta -> sin arista de regreso al depot.
        var full = new List<int>(path.Count + 1) { 0 };
        full.AddRange(path);

        bool improved = true;
        int guard = 0;
        while (improved && guard++ < 80)
        {
            improved = false;
            for (int i = 1; i < full.Count - 1; i++)
            {
                for (int j = i + 1; j < full.Count; j++)
                {
                    int a = full[i - 1];
                    int b = full[i];
                    int c = full[j];
                    bool hasNext = j + 1 < full.Count;
                    int d = hasNext ? full[j + 1] : -1;

                    double before = cost[a][b] + (hasNext ? cost[c][d] : 0);
                    double after = cost[a][c] + (hasNext ? cost[b][d] : 0);

                    if (after + 1e-9 < before)
                    {
                        full.Reverse(i, j - i + 1);
                        improved = true;
                    }
                }
            }
        }

        path.Clear();
        for (int k = 1; k < full.Count; k++) path.Add(full[k]);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        double dLat = Math.PI * (lat2 - lat1) / 180.0;
        double dLon = Math.PI * (lon2 - lon1) / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Math.PI * lat1 / 180.0) * Math.Cos(Math.PI * lat2 / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public List<Order> OptimizeRoute(List<Order> orders, double startLat, double startLng)
    {
        if (orders == null || !orders.Any()) return new List<Order>();

        var withCoords = orders.Where(o => o.Client?.Latitude != null && o.Client?.Longitude != null).ToList();
        var withoutCoords = orders.Where(o => o.Client?.Latitude == null || o.Client?.Longitude == null).ToList();

        var optimized = new List<Order>();
        var remaining = new List<Order>(withCoords);
        double currentLat = startLat;
        double currentLng = startLng;

        while (remaining.Any())
        {
            Order? nearest = null;
            double minDistance = double.MaxValue;
            int nearestIdx = -1;
            for (int i = 0; i < remaining.Count; i++)
            {
                var order = remaining[i];
                double dist = HaversineKm(currentLat, currentLng, order.Client.Latitude!.Value, order.Client.Longitude!.Value);
                if (dist < minDistance) { minDistance = dist; nearest = order; nearestIdx = i; }
            }

            if (nearest != null)
            {
                optimized.Add(nearest);
                currentLat = nearest.Client.Latitude!.Value;
                currentLng = nearest.Client.Longitude!.Value;
                remaining.RemoveAt(nearestIdx);
            }
        }

        optimized.AddRange(withoutCoords);
        return optimized;
    }
}
