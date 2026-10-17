using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Cleanii.Core.Visualization;

namespace Cleanii.App.Controls;

/// <summary>Ein Rechteck der Treemap. <paramref name="Key"/> ist null bei nicht auswählbaren Sammelflächen.</summary>
public sealed record TreemapItem(object? Key, string Label, string SizeText, double Weight, Brush Fill);

/// <summary>
/// Treemap der Einträge eines Ordners: Fläche proportional zur Größe, Farbe nach Kategorie.
/// Klick wählt aus, Doppelklick löst <see cref="ActivateCommand"/> aus (z. B. Ordner öffnen).
/// </summary>
public sealed class Treemap : ThemeAwareElement
{
    private const double Gap = 1;
    private const double CornerRadius = 4;
    private const double LabelPadding = 8;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<TreemapItem>), typeof(Treemap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(object), typeof(Treemap),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HoveredKeyProperty = DependencyProperty.Register(
        nameof(HoveredKey), typeof(object), typeof(Treemap),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand), typeof(ICommand), typeof(Treemap), new PropertyMetadata(null));

    private (TreemapItem Item, LayoutRect Rect)[] _layout = Array.Empty<(TreemapItem, LayoutRect)>();

    public IReadOnlyList<TreemapItem>? Items
    {
        get => (IReadOnlyList<TreemapItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public object? SelectedKey
    {
        get => GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    public object? HoveredKey
    {
        get => GetValue(HoveredKeyProperty);
        set => SetValue(HoveredKeyProperty, value);
    }

    public ICommand? ActivateCommand
    {
        get => (ICommand?)GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(
            double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        _layout = Array.Empty<(TreemapItem, LayoutRect)>();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        IReadOnlyList<TreemapItem> items = Items ?? Array.Empty<TreemapItem>();
        LayoutRect[] rects = SquarifiedTreemap.Layout(items.Select(i => i.Weight).ToArray(), new LayoutRect(0, 0, width, height));

        var accent = (Brush)FindResource("AccentBrush");
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceRegular = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var layout = new List<(TreemapItem, LayoutRect)>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            LayoutRect rect = rects[i];
            if (rect.Width < 1 || rect.Height < 1)
            {
                continue;
            }

            TreemapItem item = items[i];
            layout.Add((item, rect));

            var tile = new Rect(rect.X + Gap, rect.Y + Gap, Math.Max(0, rect.Width - 2 * Gap), Math.Max(0, rect.Height - 2 * Gap));
            if (tile.Width < 1 || tile.Height < 1)
            {
                continue;
            }

            dc.DrawRoundedRectangle(item.Fill, null, tile, CornerRadius, CornerRadius);

            bool hovered = item.Key is not null && Equals(item.Key, HoveredKey);
            if (hovered)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, tile, CornerRadius, CornerRadius);
            }

            if (item.Key is not null && Equals(item.Key, SelectedKey))
            {
                dc.DrawRoundedRectangle(null, new Pen(accent, 2), Rect.Inflate(tile, -1, -1), CornerRadius, CornerRadius);
            }

            DrawLabel(dc, item, tile, typeface, typefaceRegular, pixelsPerDip);
        }

        _layout = layout.ToArray();
    }

    private static void DrawLabel(DrawingContext dc, TreemapItem item, Rect tile, Typeface bold, Typeface regular, double pixelsPerDip)
    {
        double maxWidth = tile.Width - 2 * LabelPadding;
        if (maxWidth < 40 || tile.Height < 26)
        {
            return;
        }

        Brush textBrush = IsLight(item.Fill) ? DarkText : LightText;
        var name = new FormattedText(item.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 12, textBrush, pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = 18,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(name, new Point(tile.X + LabelPadding, tile.Y + 6));

        if (tile.Height >= 46)
        {
            var size = new FormattedText(item.SizeText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, regular, 11, textBrush, pixelsPerDip)
            {
                MaxTextWidth = maxWidth,
                MaxTextHeight = 16,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(size, new Point(tile.X + LabelPadding, tile.Y + 24));
        }
    }

    private static readonly Brush DarkText = Frozen(Color.FromRgb(0x0E, 0x10, 0x13));
    private static readonly Brush LightText = Frozen(Color.FromRgb(0xE6, 0xE9, 0xEF));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Relative Luminanz nach WCAG; helle Flächen bekommen dunklen Text.</summary>
    private static bool IsLight(Brush fill)
    {
        if (fill is not SolidColorBrush solid)
        {
            return false;
        }

        static double Linear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        Color color = solid.Color;
        double luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        return luminance > 0.25;
    }

    private TreemapItem? ItemAt(Point point)
    {
        foreach ((TreemapItem item, LayoutRect rect) in _layout)
        {
            if (point.X >= rect.X && point.X < rect.X + rect.Width && point.Y >= rect.Y && point.Y < rect.Y + rect.Height)
            {
                return item;
            }
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        TreemapItem? item = ItemAt(e.GetPosition(this));
        SetCurrentValue(HoveredKeyProperty, item?.Key);
        Cursor = item?.Key is null ? Cursors.Arrow : Cursors.Hand;
        string? tip = item is null ? null : $"{item.Label} · {item.SizeText}";
        if (!Equals(ToolTip, tip))
        {
            ToolTip = tip; // nur bei Änderung setzen, sonst flackert der Tooltip
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetCurrentValue(HoveredKeyProperty, null);
        Cursor = Cursors.Arrow;
        ToolTip = null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        TreemapItem? item = ItemAt(e.GetPosition(this));
        if (item?.Key is null)
        {
            return;
        }

        SetCurrentValue(SelectedKeyProperty, item.Key);
        if (e.ClickCount == 2 && ActivateCommand?.CanExecute(item.Key) == true)
        {
            ActivateCommand.Execute(item.Key);
        }
    }
}
