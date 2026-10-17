using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;

namespace HokiiCleaner.App.Infrastructure;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// Wählt das Farbthema (Dunkel oder Hell) und tauscht zur Laufzeit das Ressourcenwörterbuch an Index 0 aus.
/// Im Modus "System" folgt die App der Windows-Einstellung "App-Modus". Die Wahl wird in
/// <c>%LOCALAPPDATA%\HokiiCleaner\settings.json</c> gespeichert; das ist die einzige Datei, die HokiiCleaner schreibt.
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HokiiCleaner", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Settings(ThemeMode Theme);

    private static bool _persist = true;
    private static bool _initialized;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Hinweis, falls die Einstellungen nicht gelesen oder geschrieben werden konnten; sonst <c>null</c>.</summary>
    public static string? Warning { get; private set; }

    public static event EventHandler? Changed;

    /// <param name="forced">Erzwingt ein Thema ohne es zu speichern (Diagnose-Parameter).</param>
    public static void Initialize(ThemeMode? forced = null)
    {
        _persist = forced is null;
        Mode = forced ?? Load();

        if (!_initialized)
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                {
                    Application.Current?.Dispatcher.BeginInvoke(Apply);
                }
            };
            _initialized = true;
        }

        Apply();
    }

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        if (_persist)
        {
            Save();
        }

        Apply();
    }

    /// <summary>Windows-Einstellung "App-Modus". Fehlt der Wert (ältere Windows-Version), gilt Dunkel.</summary>
    public static AppTheme ReadSystemTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0 ? AppTheme.Light : AppTheme.Dark;
    }

    private static void Apply()
    {
        AppTheme theme = Mode switch
        {
            ThemeMode.Light => AppTheme.Light,
            ThemeMode.Dark => AppTheme.Dark,
            _ => ReadSystemTheme(),
        };

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(theme == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative),
        };
        Application.Current.Resources.MergedDictionaries[0] = dictionary;

        Current = theme;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static ThemeMode Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return ThemeMode.System;
            }

            Settings? settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOptions);
            return settings?.Theme ?? ThemeMode.System;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Warning = $"Einstellungen nicht lesbar, Standard verwendet: {ex.Message}";
            return ThemeMode.System;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(Mode), JsonOptions));
            Warning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warning = $"Einstellungen nicht gespeichert: {ex.Message}";
        }
    }
}
