using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HokiiCleaner.Actions;

/// <summary>Was ein Laufwerk über seinen Papierkorb sagt.</summary>
/// <param name="WhyNot">Grund, wenn Löschen auf diesem Laufwerk nicht in einem Papierkorb landet; sonst <c>null</c>.</param>
/// <param name="MaxBytes">Obergrenze des Papierkorbs dieses Laufwerks; <c>null</c>, wenn unbekannt.</param>
public sealed record BinDriveInfo(string? WhyNot, long? MaxBytes);

public interface IBinPolicy
{
    BinDriveInfo Describe(string path);
}

/// <summary>
/// Liest die Papierkorb-Einstellungen von Windows: ob das Laufwerk einen Papierkorb hat (feste Laufwerke), ob "Dateien sofort
/// löschen" eingestellt ist und wie groß der Papierkorb höchstens werden darf. Einträge, die größer sind, löscht Windows endgültig.
/// </summary>
public sealed class SystemBinPolicy : IBinPolicy
{
    private const string BitBucketKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    public BinDriveInfo Describe(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        if (root.Length == 0 || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new BinDriveInfo("Netzwerkpfade haben keinen Papierkorb.", null);
        }

        try
        {
            DriveType type = new DriveInfo(root).DriveType;
            if (type != DriveType.Fixed)
            {
                return new BinDriveInfo($"{root.TrimEnd('\\')} ist kein festes Laufwerk; dort gibt es keinen Papierkorb, Löschen wäre endgültig.", null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new BinDriveInfo($"Laufwerk nicht lesbar: {ex.Message}", null);
        }

        if (ReadPolicyFlag(Registry.CurrentUser) || ReadPolicyFlag(Registry.LocalMachine))
        {
            return new BinDriveInfo("Eine Richtlinie verbietet den Papierkorb (\"Dateien nicht in den Papierkorb verschieben\"); Löschen wäre endgültig.", null);
        }

        string? guid = VolumeGuid(root);
        if (guid is null)
        {
            return new BinDriveInfo(null, null);
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BitBucketKey + @"\{" + guid + "}");
        if (key is null)
        {
            return new BinDriveInfo(null, null);
        }

        if (key.GetValue("NukeOnDelete") is int nuke && nuke != 0)
        {
            return new BinDriveInfo($"Für {root.TrimEnd('\\')} ist \"Dateien sofort löschen, nicht in den Papierkorb verschieben\" eingestellt; Löschen wäre endgültig.", null);
        }

        long? max = key.GetValue("MaxCapacity") is int megabytes && megabytes > 0 ? megabytes * 1024L * 1024L : null;
        return new BinDriveInfo(null, max);
    }

    private static bool ReadPolicyFlag(RegistryKey hive)
    {
        using RegistryKey? key = hive.OpenSubKey(PolicyKey);
        return key?.GetValue("NoRecycleFiles") is int value && value != 0;
    }

    private static string? VolumeGuid(string root)
    {
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, name, name.Capacity))
        {
            return null;
        }

        // \\?\Volume{guid}\
        string value = name.ToString();
        int open = value.IndexOf('{');
        int close = value.IndexOf('}');
        return open >= 0 && close > open ? value.Substring(open + 1, close - open - 1).ToLowerInvariant() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, StringBuilder volumeName, int bufferLength);
}
