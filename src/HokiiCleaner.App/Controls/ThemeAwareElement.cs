using System.Windows;
using HokiiCleaner.App.Infrastructure;

namespace HokiiCleaner.App.Controls;

/// <summary>
/// Basis für selbst zeichnende Elemente: Sie lesen Farben beim Zeichnen aus den Ressourcen und
/// zeichnen sich bei einem Theme-Wechsel neu, solange sie im Fenster sind.
/// </summary>
public abstract class ThemeAwareElement : FrameworkElement
{
    protected ThemeAwareElement()
    {
        Loaded += (_, _) => ThemeService.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
}
