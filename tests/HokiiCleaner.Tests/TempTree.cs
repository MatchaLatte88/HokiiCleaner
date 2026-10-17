using System.Diagnostics;

namespace HokiiCleaner.Tests;

/// <summary>Temporäres Verzeichnis für einen Test; räumt beim Dispose auf, auch gesperrte Ordner.</summary>
internal sealed class TempTree : IDisposable
{
    private readonly List<string> _lockedDirectories = new();
    private readonly List<string> _junctions = new();

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "HokiiCleaner.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    public string CreateFile(string relativePath, long length)
    {
        string path = Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(length);
        return path;
    }

    public string CreateDirectory(string relativePath)
    {
        string path = Combine(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateJunction(string linkRelativePath, string targetRelativePath)
    {
        string link = Combine(linkRelativePath);
        TempTree.Run("cmd", $"/c mklink /J \"{link}\" \"{Combine(targetRelativePath)}\"");
        _junctions.Add(link);
        return link;
    }

    /// <summary>Verweigert dem aktuellen Benutzer das Auflisten des Ordners.</summary>
    public void DenyListing(string directory)
    {
        Run("icacls", $"\"{directory}\" /deny \"{Environment.UserName}:(RD)\"");
        _lockedDirectories.Add(directory);
    }

    public static string Run(string fileName, string arguments)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} nicht startbar.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} endete mit {process.ExitCode}: {output}");
        }

        return output;
    }

    public void Dispose()
    {
        foreach (string directory in _lockedDirectories)
        {
            Run("icacls", $"\"{directory}\" /remove:d \"{Environment.UserName}\"");
        }

        // rmdir auf einer Junction entfernt nur den Link, nie das Ziel.
        foreach (string junction in _junctions)
        {
            Run("cmd", $"/c rmdir \"{junction}\"");
        }

        Directory.Delete(Root, recursive: true);
    }
}
