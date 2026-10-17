namespace Cleanii.Core.Diagnostics;

/// <summary>Liest die Kodierung des Registry-Werts unter <c>Explorer\StartupApproved</c>.</summary>
public static class StartupApproval
{
    /// <summary>
    /// Kein Wert: aktiviert. Sonst entscheidet das niedrigste Bit des ersten Bytes: 0 = aktiviert, 1 = deaktiviert.
    /// Das ist nicht von Microsoft dokumentiert, sondern eine Beobachtung (Werte 02/06 aktiviert, 01/03/07 deaktiviert);
    /// auf einem Testrechner liefen nur Programme mit geradem Wert.
    /// </summary>
    public static bool IsEnabled(byte[]? data) => data is null || data.Length == 0 || (data[0] & 1) == 0;
}

/// <summary>Findet in einem Autostart-Befehl die ausführbare Datei.</summary>
public static class StartupCommandParser
{
    /// <summary>
    /// Liefert den Pfad der ausführbaren Datei aus einem Befehl wie <c>"C:\Pfad mit Leerzeichen\a.exe" /arg</c>
    /// oder <c>C:\Pfad\a.exe -silent</c>; Umgebungsvariablen werden aufgelöst. <c>null</c>, wenn der Befehl leer ist.
    /// </summary>
    public static string? ResolveExecutable(string command, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        string text = Environment.ExpandEnvironmentVariables(command ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            return end < 0 ? text.Substring(1) : text.Substring(1, end - 1);
        }

        // Ohne Anführungszeichen können Pfade Leerzeichen enthalten: von kurz nach lang den ersten existierenden Treffer nehmen.
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ' && i > 0)
            {
                string candidate = text.Substring(0, i);
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (fileExists(text))
        {
            return text;
        }

        int firstSpace = text.IndexOf(' ');
        return firstSpace < 0 ? text : text.Substring(0, firstSpace);
    }

    /// <summary>
    /// True/false für absolute Pfade je nach Existenz. Bei Namen ohne Pfad (z. B. <c>rundll32</c>) wird über PATH gesucht;
    /// ohne Treffer ist das Ergebnis <c>null</c> (unbekannt), nicht false.
    /// </summary>
    public static bool? Exists(string? executable, Func<string, bool>? fileExists = null, IEnumerable<string>? searchDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        fileExists ??= File.Exists;
        if (executable.Contains('\\') || executable.Contains('/'))
        {
            return fileExists(executable);
        }

        searchDirectories ??= (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (string directory in searchDirectories)
        {
            string combined = Path.Combine(directory.Trim('"'), executable);
            if (fileExists(combined) || fileExists(combined + ".exe"))
            {
                return true;
            }
        }

        return null;
    }
}
