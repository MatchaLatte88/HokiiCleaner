using System.IO;
using Cleanii.Core;

namespace Cleanii.App.ViewModels;

public sealed class DriveViewModel
{
    public DriveViewModel(DriveInfo drive)
    {
        RootPath = drive.RootDirectory.FullName;
        Label = RootPath.TrimEnd('\\');
        TotalBytes = drive.TotalSize;
        FreeBytes = drive.AvailableFreeSpace;
    }

    public string RootPath { get; }

    /// <summary>Kurzname wie "C:".</summary>
    public string Label { get; }

    public long TotalBytes { get; }

    public long FreeBytes { get; }

    public long UsedBytes => TotalBytes - FreeBytes;

    public string Summary => $"{ByteFormat.Format(UsedBytes)} von {ByteFormat.Format(TotalBytes)}";

    public static IReadOnlyList<DriveViewModel> LoadFixedDrives()
        => DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveViewModel(d))
            .ToArray();
}
