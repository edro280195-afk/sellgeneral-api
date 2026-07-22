using System.Text.Json;
using System.Text.RegularExpressions;
using EntregasApi.Models;

namespace EntregasApi.Services;

public interface ILabelTemplateDesignValidator
{
    LabelTemplateValidationResult Validate(string designJson, LabelTemplateKind kind, LabelMediaSize mediaSize);
}

public sealed record LabelTemplateValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlySet<Guid> AssetIds)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Valida el contrato de etiquetas antes de guardar o publicar un diseño.</summary>
public sealed partial class LabelTemplateDesignValidator : ILabelTemplateDesignValidator
{
    private const int MaxDesignLength = 250_000;
    private const int MaxElements = 80;
    private const double Tolerance = 0.05;
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "text", "data", "image", "qr", "barcode", "shape", "line"
    };

    public LabelTemplateValidationResult Validate(string designJson, LabelTemplateKind kind, LabelMediaSize mediaSize)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var assets = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(designJson))
        {
            errors.Add("El diseño de la etiqueta es obligatorio.");
            return new(errors, warnings, assets);
        }
        if (designJson.Length > MaxDesignLength)
        {
            errors.Add("El diseño supera el tamaño máximo permitido.");
            return new(errors, warnings, assets);
        }

        try
        {
            using var document = JsonDocument.Parse(designJson, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("El diseño debe ser un objeto JSON.");
                return new(errors, warnings, assets);
            }
            if (!TryInt(root, "schemaVersion", out var schemaVersion) || schemaVersion != 1)
            {
                errors.Add("La versión del esquema de etiqueta no es compatible.");
            }

            var (canvasWidth, canvasHeight) = LabelTemplateProfilePolicy.GetDimensions(mediaSize);
            if (!root.TryGetProperty("canvas", out var canvas) || canvas.ValueKind != JsonValueKind.Object ||
                !TryDouble(canvas, "widthMm", out var width) || !TryDouble(canvas, "heightMm", out var height) ||
                Math.Abs(width - canvasWidth) > Tolerance || Math.Abs(height - canvasHeight) > Tolerance)
            {
                errors.Add($"El lienzo debe medir {canvasWidth:0.##} × {canvasHeight:0.##} mm para este formato.");
            }

            if (!root.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            {
                errors.Add("El diseño debe incluir la lista de elementos.");
                return new(errors, warnings, assets);
            }
            if (elements.GetArrayLength() is < 1 or > MaxElements)
            {
                errors.Add($"La etiqueta debe contener entre 1 y {MaxElements} elementos.");
                return new(errors, warnings, assets);
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var bindings = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in elements.EnumerateArray())
            {
                ValidateElement(element, ++index, canvasWidth, canvasHeight, mediaSize, ids, bindings, assets, errors, warnings);
            }
            ValidateRequiredBindings(kind, bindings, errors);
        }
        catch (JsonException)
        {
            errors.Add("El diseño no contiene JSON válido.");
        }

        return new(errors, warnings, assets);
    }

    private static void ValidateElement(
        JsonElement element,
        int index,
        double canvasWidth,
        double canvasHeight,
        LabelMediaSize mediaSize,
        ISet<string> ids,
        ISet<string> bindings,
        ISet<Guid> assets,
        ICollection<string> errors,
        ICollection<string> warnings)
    {
        var prefix = $"Elemento {index}";
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}: debe ser un objeto.");
            return;
        }
        if (!TryString(element, "id", out var id) || id.Length > 64 || !SafeIdentifier().IsMatch(id))
        {
            errors.Add($"{prefix}: id inválido.");
        }
        else if (!ids.Add(id))
        {
            errors.Add($"{prefix}: el id '{id}' está repetido.");
        }
        if (!TryString(element, "type", out var type) || !SupportedTypes.Contains(type))
        {
            errors.Add($"{prefix}: tipo de elemento no permitido.");
            return;
        }
        var x = 0d;
        var y = 0d;
        var width = 0d;
        var height = 0d;
        var hasBounds = TryDouble(element, "x", out x) && TryDouble(element, "y", out y) &&
                        TryDouble(element, "width", out width) && TryDouble(element, "height", out height);
        if (!hasBounds ||
            x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > canvasWidth + Tolerance || y + height > canvasHeight + Tolerance)
        {
            errors.Add($"{prefix}: posición o tamaño fuera del lienzo.");
        }
        if (element.TryGetProperty("rotation", out var rotation) && (!rotation.TryGetDouble(out var degrees) || degrees is < -360 or > 360))
        {
            errors.Add($"{prefix}: rotación inválida.");
        }
        var visible = !element.TryGetProperty("visible", out var visibleValue) || visibleValue.ValueKind != JsonValueKind.False;
        if (!element.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}: faltan las propiedades del elemento.");
            return;
        }

        if (type is "data" or "qr" or "barcode")
        {
            if (!TryString(properties, "binding", out var binding) || binding.Length > 100 || !SafeBinding().IsMatch(binding))
            {
                errors.Add($"{prefix}: el dato vinculado es obligatorio y no es válido.");
            }
            else if (visible)
            {
                bindings.Add(binding);
            }
        }
        else if (type == "text" && (!TryString(properties, "text", out var text) || text.Length > 1_000))
        {
            errors.Add($"{prefix}: el texto es obligatorio y debe medir máximo 1000 caracteres.");
        }
        else if (type == "image")
        {
            if (!TryString(properties, "assetId", out var assetIdText) || !Guid.TryParse(assetIdText, out var assetId))
            {
                errors.Add($"{prefix}: la imagen debe provenir de la biblioteca de la tienda.");
            }
            else
            {
                assets.Add(assetId);
            }
        }

        var (minimumQr, minimumBarcodeWidth) = LabelTemplateProfilePolicy.GetMinimumReadableSizes(mediaSize);
        if (type == "qr")
        {
            if (!visible || Math.Abs(width - height) > Tolerance || Math.Min(width, height) < minimumQr)
            {
                errors.Add($"{prefix}: el QR visible debe ser cuadrado y medir al menos {minimumQr:0.#} mm.");
            }
            if (element.TryGetProperty("rotation", out var qrRotation) && qrRotation.TryGetDouble(out var qrDegrees) && Math.Abs(qrDegrees) > Tolerance)
            {
                errors.Add($"{prefix}: el QR no debe rotarse para conservar su lectura.");
            }
            if (x < 1 || y < 1 || x + width > canvasWidth - 1 || y + height > canvasHeight - 1)
            {
                warnings.Add($"{prefix}: deja 1 mm libre alrededor del QR para una lectura más confiable.");
            }
        }
        if (type == "barcode" && (!visible || width < minimumBarcodeWidth || height < 10))
        {
            errors.Add($"{prefix}: el código de barras visible debe medir al menos {minimumBarcodeWidth:0.#} mm de ancho y 10 mm de alto.");
        }
    }

    private static void ValidateRequiredBindings(LabelTemplateKind kind, ISet<string> bindings, ICollection<string> errors)
    {
        var required = kind switch
        {
            LabelTemplateKind.InventoryBox => new[] { "box.code", "box.nfcUrl" },
            LabelTemplateKind.InventoryItem => new[] { "item.name", "item.scannableCode" },
            LabelTemplateKind.OrderPackage => new[] { "order.clientName", "package.number", "package.qrCodeValue" },
            _ => Array.Empty<string>()
        };
        foreach (var binding in required.Where(binding => !bindings.Contains(binding)))
        {
            errors.Add($"La plantilla debe conservar el dato obligatorio '{binding}'.");
        }
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(property, out var propertyValue) && propertyValue.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = propertyValue.GetString() ?? string.Empty);
    }
    private static bool TryDouble(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var propertyValue) && propertyValue.TryGetDouble(out value);
    }

    private static bool TryInt(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var propertyValue) && propertyValue.TryGetInt32(out value);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeIdentifier();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*(?:\\.[a-zA-Z][a-zA-Z0-9]*)+$")]
    private static partial Regex SafeBinding();
}
