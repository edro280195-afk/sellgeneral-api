using System.Text.Json;
using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>Diseños seguros de arranque. La marca de la tienda se resuelve como dato, no se incrusta en el código.</summary>
public static class LabelTemplateDesignFactory
{
    public static string CreateDefaultDesign(LabelTemplateKind kind, LabelMediaSize mediaSize)
    {
        return kind switch
        {
            LabelTemplateKind.OrderPackage => CreateOrderPackageDesign(mediaSize),
            LabelTemplateKind.InventoryBox => CreateInventoryBoxDesign(mediaSize),
            LabelTemplateKind.InventoryItem => CreateInventoryItemDesign(mediaSize),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string CreateOrderPackageDesign(LabelMediaSize mediaSize)
    {
        var (width, height) = LabelTemplateProfilePolicy.GetDimensions(mediaSize);
        var compact = mediaSize == LabelMediaSize.Square50x50;
        var elements = compact
            ? new object[]
            {
                Data("business-name", "business.name", 3, 3, 44, 5, 13, 700, "center"),
                Text("recipient-kicker", "ENTREGAR A", 3, 10, 25, 3, 6, 700, "left"),
                Data("client-name", "order.clientName", 3, 14, 25, 10, 13, 800, "left", wrap: true),
                Data("package-number", "package.number", 30, 11, 17, 8, 18, 800, "center", prefix: "#"),
                Qr("package-qr", "package.qrCodeValue", 28, 21, 20),
                Data("package-total", "package.total", 29, 40, 18, 3, 6, 600, "center", suffix: " BOLSAS"),
                Data("address", "order.address", 3, 27, 24, 15, 7, 600, "left", wrap: true)
            }
            : new object[]
            {
                Data("business-name", "business.name", 6, 6, 89.6, 8, 24, 800, "center"),
                Text("recipient-kicker", "ENTREGAR A", 6, 20, 54, 4, 9, 700, "left"),
                Data("client-name", "order.clientName", 6, 26, 54, 12, 21, 800, "left", wrap: true),
                Data("client-phone", "order.phone", 6, 40, 54, 5, 10, 600, "left", prefix: "Tel. "),
                Data("address", "order.address", 6, 49, 54, 28, 12, 600, "left", wrap: true),
                Text("package-kicker", "BOLSA", 67, 20, 28.6, 4, 9, 700, "center"),
                Data("package-number", "package.number", 67, 26, 28.6, 14, 36, 800, "center", prefix: "#"),
                Qr("package-qr", "package.qrCodeValue", 67, 48, 28),
                Data("package-total", "package.total", 67, 79, 28.6, 4, 8, 600, "center", suffix: " BOLSAS"),
                Text("items-kicker", "CONTENIDO", 6, 86, 54, 4, 9, 700, "left"),
                Data("items", "order.itemSummary", 6, 92, 54, 34, 10, 500, "left", wrap: true),
                Data("delivery-note", "order.deliveryInstructions", 6, 130, 89.6, 9, 8, 600, "left", wrap: true, prefix: "Nota: ")
            };

        return Serialize(width, height, elements);
    }

    private static string CreateInventoryBoxDesign(LabelMediaSize mediaSize)
    {
        var (width, height) = LabelTemplateProfilePolicy.GetDimensions(mediaSize);
        var qrSize = Math.Min(width * 0.42, height * 0.42);
        return Serialize(width, height,
        [
            Data("business-name", "business.name", 3, 3, width - 6, 5, 13, 700, "center"),
            Data("box-code", "box.code", 3, 11, width - qrSize - 8, 8, 18, 800, "left"),
            Data("box-name", "box.name", 3, 21, width - qrSize - 8, 14, 11, 700, "left", wrap: true),
            Qr("box-qr", "box.nfcUrl", width - qrSize - 3, 18, qrSize)
        ]);
    }

    private static string CreateInventoryItemDesign(LabelMediaSize mediaSize)
    {
        var (width, height) = LabelTemplateProfilePolicy.GetDimensions(mediaSize);
        return Serialize(width, height,
        [
            Data("business-name", "business.name", 3, 3, width - 6, 5, 13, 700, "center"),
            Data("item-name", "item.name", 3, 11, width - 6, 13, 15, 800, "left", wrap: true),
            Barcode("item-barcode", "item.scannableCode", 3, Math.Min(height - 19, 30), width - 6, 12),
            Data("item-code", "item.scannableCode", 3, Math.Min(height - 6, 44), width - 6, 3, 7, 600, "center")
        ]);
    }

    private static string Serialize(double width, double height, object[] elements) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        canvas = new { widthMm = width, heightMm = height, background = "#FFFFFF" },
        elements
    });

    private static object Text(string id, string text, double x, double y, double width, double height, int fontSize, int fontWeight, string align) =>
        new { id, type = "text", x, y, width, height, rotation = 0, visible = true, zIndex = 1, properties = new { text, fontSize, fontWeight, align } };

    private static object Data(string id, string binding, double x, double y, double width, double height, int fontSize, int fontWeight, string align, bool wrap = false, string? prefix = null, string? suffix = null) =>
        new { id, type = "data", x, y, width, height, rotation = 0, visible = true, zIndex = 2, properties = new { binding, fontSize, fontWeight, align, wrap, prefix, suffix } };

    private static object Qr(string id, string binding, double x, double y, double size) =>
        new { id, type = "qr", x, y, width = size, height = size, rotation = 0, visible = true, zIndex = 3, properties = new { binding, errorCorrection = "M" } };

    private static object Barcode(string id, string binding, double x, double y, double width, double height) =>
        new { id, type = "barcode", x, y, width, height, rotation = 0, visible = true, zIndex = 3, properties = new { binding, format = "CODE128", displayValue = false } };
}
