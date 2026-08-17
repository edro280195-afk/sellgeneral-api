namespace EntregasApi.Services;

/// <summary>
/// Normaliza teléfonos para identidad. Los teléfonos de Firebase y los nuevos
/// registros se guardan en E.164; se conservan conversiones legacy para poder
/// enlazar cuentas históricas que aún tienen diez dígitos nacionales.
/// </summary>
public static class PhoneNumberNormalizer
{
    private const string MexicoCountryCode = "52";
    private const int MexicoNationalLength = 10;

    /// <summary>
    /// Devuelve un teléfono E.164. Los números nacionales sin lada internacional
    /// se interpretan como mexicanos para mantener el comportamiento actual.
    /// </summary>
    public static string? NormalizeE164(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var trimmed = input.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (trimmed.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        if (digits.Length == MexicoNationalLength)
        {
            return $"+{MexicoCountryCode}{digits}";
        }

        if (digits.Length == MexicoCountryCode.Length + MexicoNationalLength &&
            digits.StartsWith(MexicoCountryCode, StringComparison.Ordinal))
        {
            return $"+{digits}";
        }

        // Permite números internacionales futuros cuando ya incluyen código de
        // país. E.164 admite como máximo 15 dígitos y no lleva extensiones.
        return digits.Length is >= 8 and <= 15 ? $"+{digits}" : null;
    }

    /// <summary>Convierte un E.164 mexicano a diez dígitos para matches legacy.</summary>
    public static string? ToMexicanNational(string? input)
    {
        var e164 = NormalizeE164(input);
        if (e164 is null || !e164.StartsWith($"+{MexicoCountryCode}", StringComparison.Ordinal))
            return null;

        var national = e164[(MexicoCountryCode.Length + 1)..];
        return national.Length == MexicoNationalLength ? national : null;
    }

    /// <summary>Compara un valor E.164 con una cuenta histórica nacional.</summary>
    public static bool MatchesStoredPhone(string? storedPhone, string e164Phone)
    {
        if (string.IsNullOrWhiteSpace(storedPhone)) return false;
        if (string.Equals(storedPhone, e164Phone, StringComparison.Ordinal)) return true;

        var national = ToMexicanNational(e164Phone);
        return national is not null &&
               string.Equals(storedPhone, national, StringComparison.Ordinal);
    }
}
