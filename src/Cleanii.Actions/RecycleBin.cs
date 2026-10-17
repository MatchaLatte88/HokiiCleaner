using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Cleanii.Actions;

public sealed record BinDriveStatus(string DriveRoot, long Bytes, long Items);

/// <summary>Ein Eintrag im Papierkorb. <see cref="OriginalPath"/> ist der Pfad vor dem Löschen.</summary>
/// <param name="SizeBytes">Größe laut Windows (Dateien); bei Ordnern <c>null</c>, bis sie gemessen ist.</param>
public sealed record BinEntry(
    string DriveRoot,
    string OriginalPath,
    DateTime DeletedUtc,
    bool IsDirectory,
    long? SizeBytes,
    string DataPath,
    string MetaPath)
{
    public string Name => Path.GetFileName(OriginalPath.TrimEnd('\\', '/'));
}

/// <param name="Orphans">
/// Protokolleinträge (<c>$I</c>) ohne Inhalt (<c>$R</c>): Überbleibsel, die Windows nicht anzeigt. Sie werden gezählt, nicht gelistet.
/// </param>
public sealed record BinListing(IReadOnlyList<BinEntry> Entries, IReadOnlyList<string> Problems, int Orphans = 0);

public enum RestoreStatus
{
    Restored,

    /// <summary>Am ursprünglichen Ort liegt inzwischen etwas anderes; nichts wurde überschrieben.</summary>
    TargetExists,

    Failed,
}

public sealed record RestoreOutcome(RestoreStatus Status, string? Detail);

/// <summary>Inhalt einer <c>$I</c>-Datei im Papierkorb: ursprünglicher Pfad, Größe und Löschzeit.</summary>
public static class RecycleBinMetadata
{
    /// <summary>
    /// Liest das Format von Windows 10/11 (Version 2: Pfadlänge in Zeichen, danach der Pfad) und das ältere Format
    /// (Version 1: fester Puffer). Unbekannte Versionen und abgeschnittene Dateien liefern <c>false</c>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out string originalPath, out DateTime deletedUtc, out long size)
    {
        originalPath = string.Empty;
        deletedUtc = default;
        size = 0;
        if (data.Length < 28)
        {
            return false;
        }

        long version = BitConverter.ToInt64(data);
        size = BitConverter.ToInt64(data[8..]);
        long fileTime = BitConverter.ToInt64(data[16..]);
        if (fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
        {
            return false;
        }

        deletedUtc = DateTime.FromFileTimeUtc(fileTime);
        ReadOnlySpan<byte> text;
        if (version == 2)
        {
            int length = BitConverter.ToInt32(data[24..]);
            int bytes = length * 2;
            if (length <= 0 || 28 + bytes > data.Length)
            {
                return false;
            }

            text = data.Slice(28, bytes);
        }
        else if (version == 1)
        {
            text = data[24..];
        }
        else
        {
            return false;
        }

        originalPath = Encoding.Unicode.GetString(text).Split('\0')[0];
        return originalPath.Length > 0;
    }
}

/// <summary>
/// Der Papierkorb des angemeldeten Benutzers: Größe und Anzahl (Windows), Inhalt (die <c>$I</c>/<c>$R</c>-Dateien im Ordner
/// <c>$Recycle.Bin\&lt;SID&gt;</c>), Wiederherstellen und Leeren. Es werden nur Einträge des eigenen Benutzers gelesen.
/// </summary>
public static class RecycleBin
{
    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;

    /// <summary>Anzahl und Größe laut Windows. Wirft <see cref="Win32Exception"/>, wenn Windows die Abfrage ablehnt.</summary>
    public static BinDriveStatus Query(string driveRoot)
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("Die Papierkorb-Abfrage ist nur in einem 64-Bit-Prozess umgesetzt.");
        }

        var info = new BinQueryInfo { cbSize = Marshal.SizeOf<BinQueryInfo>() };
        int hr = SHQueryRecycleBinW(driveRoot, ref info);
        if (hr != 0)
        {
            throw new Win32Exception(hr, $"Papierkorb von {driveRoot} nicht abfragbar (HRESULT 0x{hr:X}).");
        }

        return new BinDriveStatus(driveRoot, info.i64Size, info.i64NumItems);
    }

    /// <summary>Gesamtzahl der Einträge auf den Laufwerken; <c>null</c>, wenn Windows die Abfrage eines Laufwerks ablehnt.</summary>
    public static long? CountItems(IReadOnlyList<string> driveRoots)
    {
        long total = 0;
        foreach (string drive in driveRoots)
        {
            try
            {
                total += Query(drive).Items;
            }
            catch (Win32Exception)
            {
                return null;
            }
        }

        return total;
    }

    /// <summary>Leert den Papierkorb endgültig (eines Laufwerks oder aller). Ohne Rückfrage; die Bestätigung gehört in die Oberfläche.</summary>
    public static void Empty(string? driveRoot)
    {
        int hr = SHEmptyRecycleBinW(IntPtr.Zero, driveRoot, NoConfirmation | NoProgressUi | NoSound);

        // S_FALSE oder E_UNEXPECTED melden manche Windows-Versionen, wenn schon nichts mehr drin war.
        if (hr is 0 or 1 or unchecked((int)0x8000FFFF))
        {
            return;
        }

        throw new Win32Exception(hr, $"Der Papierkorb ließ sich nicht leeren (HRESULT 0x{hr:X}).");
    }

    /// <summary>Der Ordner des Benutzers im Papierkorb eines Laufwerks, z. B. <c>C:\$Recycle.Bin\S-1-5-21-…</c>.</summary>
    public static string UserFolder(string driveRoot, string? sid = null)
    {
        if (string.IsNullOrEmpty(sid))
        {
            sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("Die Benutzerkennung (SID) ist nicht ermittelbar.");
        }

        return Path.Combine(driveRoot, "$Recycle.Bin", sid);
    }

    /// <summary>Liest die Einträge des Benutzers auf einem Laufwerk. Unlesbare Einträge erscheinen unter <see cref="BinListing.Problems"/>.</summary>
    public static BinListing List(string driveRoot, string? userFolder = null)
    {
        string folder = string.IsNullOrEmpty(userFolder) ? UserFolder(driveRoot) : userFolder;
        var entries = new List<BinEntry>();
        var problems = new List<string>();
        int orphans = 0;
        if (!Directory.Exists(folder))
        {
            return new BinListing(entries, problems);
        }

        IEnumerable<string> metaFiles;
        try
        {
            metaFiles = Directory.EnumerateFiles(folder, "$I*").ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            problems.Add($"{folder}: {ex.Message}");
            return new BinListing(entries, problems, orphans);
        }

        foreach (string meta in metaFiles)
        {
            string fileName = Path.GetFileName(meta);
            string data = Path.Combine(folder, "$R" + fileName[2..]);
            try
            {
                if (!RecycleBinMetadata.TryParse(File.ReadAllBytes(meta), out string original, out DateTime deleted, out long size))
                {
                    problems.Add($"{fileName}: Format nicht erkannt.");
                    continue;
                }

                bool isDirectory = Directory.Exists(data);
                if (!isDirectory && !File.Exists(data))
                {
                    orphans++; // Windows zeigt solche Reste selbst nicht an
                    continue;
                }

                entries.Add(new BinEntry(driveRoot, original, deleted, isDirectory, isDirectory ? null : new FileInfo(data).Length, data, meta));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                problems.Add($"{fileName}: {ex.Message}");
            }
        }

        return new BinListing(entries, problems, orphans);
    }

    /// <summary>Summiert die Dateigrößen eines Ordners im Papierkorb. Gesperrte Unterordner zählen nicht mit; sie sind im Rückgabewert vermerkt.</summary>
    public static (long Bytes, int UnreadableFolders) MeasureDirectory(string path, CancellationToken ct)
    {
        long bytes = 0;
        int unreadable = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (entry is FileInfo file)
                    {
                        bytes += file.Length;
                    }
                    else
                    {
                        pending.Push(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                unreadable++;
            }
        }

        return (bytes, unreadable);
    }

    /// <summary>Legt einen Eintrag an seinen ursprünglichen Ort zurück. Überschreibt nichts.</summary>
    public static RestoreOutcome Restore(BinEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string target = entry.OriginalPath;
        if (!Path.IsPathFullyQualified(target))
        {
            return new RestoreOutcome(RestoreStatus.Failed, "Der ursprüngliche Pfad ist nicht vollständig.");
        }

        if (!string.Equals(Path.GetPathRoot(target), entry.DriveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new RestoreOutcome(RestoreStatus.Failed, "Der ursprüngliche Ort liegt auf einem anderen Laufwerk als der Papierkorb.");
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            return new RestoreOutcome(RestoreStatus.TargetExists, "Am ursprünglichen Ort liegt inzwischen etwas anderes.");
        }

        try
        {
            string? parent = Path.GetDirectoryName(target);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            if (entry.IsDirectory)
            {
                Directory.Move(entry.DataPath, target);
            }
            else
            {
                File.Move(entry.DataPath, target);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new RestoreOutcome(RestoreStatus.Failed, ex.Message);
        }

        try
        {
            File.Delete(entry.MetaPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new RestoreOutcome(RestoreStatus.Restored, $"Wiederhergestellt, aber der Papierkorb-Eintrag ließ sich nicht entfernen: {ex.Message}");
        }

        return new RestoreOutcome(RestoreStatus.Restored, null);
    }

    // shellapi.h packt SHQUERYRBINFO unter 64 Bit mit 8 (cbSize = 24), unter 32 Bit mit 1 (cbSize = 20). Umgesetzt ist nur 64 Bit.
    [StructLayout(LayoutKind.Sequential)]
    private struct BinQueryInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref BinQueryInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);
}
