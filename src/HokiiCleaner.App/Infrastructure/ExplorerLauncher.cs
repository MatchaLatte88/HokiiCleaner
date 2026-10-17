using System.ComponentModel;
using System.Diagnostics;

namespace HokiiCleaner.App.Infrastructure;

/// <summary>Zeigt einen Pfad im Windows-Explorer. Verändert nichts.</summary>
public static class ExplorerLauncher
{
    /// <summary>Öffnet den Explorer und markiert die Datei oder den Ordner. Wirft bei Startproblemen.</summary>
    /// <exception cref="Win32Exception">Der Explorer konnte nicht gestartet werden.</exception>
    public static void Reveal(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }
}
