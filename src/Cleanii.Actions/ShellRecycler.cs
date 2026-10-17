using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cleanii.Actions;

/// <summary>
/// Legt Einträge über die Windows-Shell in den Papierkorb (<c>SHFileOperation</c> mit <c>FOF_ALLOWUNDO</c>). Ein Eintrag, der
/// nicht in den Papierkorb passt, würde sonst stillschweigend endgültig gelöscht; <c>FOF_WANTNUKEWARNING</c> lässt Windows
/// stattdessen fragen. Wer vorher plant (<see cref="RecycleEngine.Plan"/>), lehnt solche Einträge schon vorab ab.
/// </summary>
public sealed class ShellRecycler : IRecycler
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    public RecycleOutcome Recycle(string path, bool isDirectory)
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("Das Verschieben in den Papierkorb ist nur in einem 64-Bit-Prozess umgesetzt.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Kein vollständiger Pfad: {path}", nameof(path));
        }

        // pFrom ist eine Liste, die mit zwei Nullzeichen endet. Ein Pfad mit eingebettetem Nullzeichen würde sie verkürzen.
        if (path.Contains('\0'))
        {
            throw new ArgumentException("Der Pfad enthält ein Nullzeichen.", nameof(path));
        }

        var operation = new ShFileOpStruct
        {
            hwnd = IntPtr.Zero,
            wFunc = FoDelete,
            pFrom = path + "\0\0",
            pTo = null,
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent | FofWantNukeWarning,
        };

        int code = SHFileOperationW(ref operation);
        if (operation.fAnyOperationsAborted)
        {
            return new RecycleOutcome(false, "Abgebrochen (Windows hat nachgefragt und der Vorgang wurde nicht fortgesetzt).");
        }

        return code == 0
            ? RecycleOutcome.Done
            : new RecycleOutcome(false, Describe(code));
    }

    private static string Describe(int code)
    {
        // SHFileOperation liefert eigene Fehlercodes und teils Win32-Fehler.
        string text = code switch
        {
            0x74 => "Das Stammverzeichnis eines Laufwerks lässt sich nicht verschieben.",
            0x75 => "Der Vorgang wurde abgebrochen.",
            0x78 => "Zugriff verweigert.",
            0x79 => "Der Pfad ist länger als MAX_PATH; diese Windows-Funktion kommt damit nicht zurecht.",
            0x7C => "Ungültiger Pfad.",
            0x81 => "Ein Dateiname ist länger als MAX_PATH; diese Windows-Funktion kommt damit nicht zurecht.",
            0x402 => "Unbekannter Fehler.",
            0x10000 => "Unbestimmter Fehler beim Ziel (Papierkorb).",
            _ => new Win32Exception(code).Message,
        };

        return $"{text} (Code 0x{code:X})";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct operation);
}
