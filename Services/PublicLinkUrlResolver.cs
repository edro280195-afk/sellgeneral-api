using EntregasApi.Models;
using Microsoft.Extensions.Configuration;

namespace EntregasApi.Services;

/// <summary>Resuelve el origen público de la tienda para enlaces compartibles.</summary>
public static class PublicLinkUrlResolver
{
    public const string PlatformBaseUrl = "https://app.nenisapp.com";

    /// <summary>
    /// Usa el dominio configurado para el negocio. Si no existe o conserva el
    /// dominio anterior de Regi Bazar, usa el host público de Neni's App.
    /// </summary>
    public static string Resolve(Business? business, IConfiguration? configuration = null)
    {
        var candidates = new[]
        {
            business?.FrontendUrl,
            configuration?["App:FrontendUrl"],
            PlatformBaseUrl
        };

        foreach (var candidate in candidates)
        {
            if (TryNormalize(candidate, out var normalized))
            {
                return normalized;
            }
        }

        return PlatformBaseUrl;
    }

    public static bool IsLegacyRegiBazarDomain(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("regibazar.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".regibazar.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || IsLegacyRegiBazarDomain(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isSecure = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLocalDevelopment = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        var isOriginOnly = uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo);

        if ((!isSecure && !isLocalDevelopment) || !isOriginOnly)
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }
}
