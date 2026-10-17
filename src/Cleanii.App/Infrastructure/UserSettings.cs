using System.IO;
using System.Text.Json;

namespace Cleanii.App.Infrastructure;

/// <summary>
/// Einstellungen des Benutzers, derzeit nur die geschützten Ordner (lassen sich nie für den Papierkorb vormerken).
/// Liegt in %LOCALAPPDATA%\Cleanii\settings.json und wird nur beim Ändern der Liste geschrieben.
/// </summary>
public sealed class UserSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cleanii", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private sealed record Stored(List<string>? ProtectedFolders);

    public List<string> ProtectedFolders { get; } = new();

    /// <summary>Liest die Einstellungen. Eine fehlende Datei ergibt die Voreinstellung; eine beschädigte wird gemeldet und beim nächsten Speichern ersetzt.</summary>
    /// <param name="problem">Grund, warum die Datei nicht gelesen werden konnte; sonst <c>null</c>.</param>
    public static UserSettings Load(out string? problem)
    {
        problem = null;
        var settings = new UserSettings();
        if (!File.Exists(FilePath))
        {
            return settings;
        }

        try
        {
            Stored? stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), Options);
            foreach (string folder in stored?.ProtectedFolders ?? new List<string>())
            {
                if (Path.IsPathFullyQualified(folder))
                {
                    settings.ProtectedFolders.Add(Path.GetFullPath(folder).TrimEnd('\\'));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"Die Einstellungen ({FilePath}) ließen sich nicht lesen und werden ignoriert: {ex.Message}";
        }

        return settings;
    }

    /// <summary>Schreibt die Einstellungen. Wirft bei Schreibfehlern (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>).</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(ProtectedFolders), Options));
    }
}
