using System.Globalization;

namespace HokiiCleaner.Core;

public static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>
    /// Formatiert Bytes mit binären Einheiten (1 KB = 1024 B, wie im Explorer).
    /// GB und TB mit zwei, MB und KB ohne Nachkommastellen. Standard ist die aktuelle Kultur.
    /// </summary>
    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        (string number, string unit) = Split(bytes, provider);
        return $"{number} {unit}";
    }

    /// <summary>Zahl und Einheit getrennt, z. B. <c>("800,65", "GB")</c>, für Anzeigen mit großer Zahl und kleiner Einheit.</summary>
    public static (string Number, string Unit) Split(long bytes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;

        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = unit >= 3 ? "N2" : "N0";
        return (value.ToString(format, provider), Units[unit]);
    }
}
