using EntregasApi.Models;

namespace EntregasApi.Services;

public static class LabelTemplateProfilePolicy
{
    public static (double Width, double Height) GetDimensions(LabelMediaSize mediaSize) => mediaSize switch
    {
        LabelMediaSize.Square50x50 => (50, 50),
        LabelMediaSize.Shipping4x6 => (101.6, 152.4),
        _ => throw new ArgumentOutOfRangeException(nameof(mediaSize))
    };

    public static (double MinimumQr, double MinimumBarcodeWidth) GetMinimumReadableSizes(LabelMediaSize mediaSize) => mediaSize switch
    {
        LabelMediaSize.Square50x50 => (20, 30),
        LabelMediaSize.Shipping4x6 => (28, 45),
        _ => throw new ArgumentOutOfRangeException(nameof(mediaSize))
    };
}
