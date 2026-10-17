using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HokiiCleaner.App.Infrastructure;

/// <summary>Kleine Fenstereffekte über den Desktop-Fenster-Manager. Rein kosmetisch.</summary>
internal static class WindowEffects
{
    private const int WindowCornerPreference = 33;
    private const int RoundCorners = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Runde Fensterecken unter Windows 11. Ältere Windows-Versionen ignorieren das; das Fenster bleibt dort eckig.</summary>
    public static void UseRoundedCorners(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        int preference = RoundCorners;

        // Rückgabewert bewusst ignoriert: Auf Windows 10 ist das Attribut unbekannt, die App läuft unverändert.
        _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref preference, sizeof(int));
    }
}
