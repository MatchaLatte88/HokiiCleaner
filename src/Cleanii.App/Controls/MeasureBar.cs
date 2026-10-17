using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Cleanii.Core.Categorization;

namespace Cleanii.App.Controls;

/// <summary>Ein Abschnitt des Messbands. <paramref name="Category"/> ist null bei nicht zuordenbaren Abschnitten.</summary>
public sealed record BarSegment(FileCategory? Category, long Bytes, Brush Fill, bool Hatched = false);

/// <summary>
/// Das Laufwerk als Messband: Segmente dicht nebeneinander, darunter eine Skala in Größeneinheiten.
/// Hover und Auswahl heben ein Segment hervor, der Rest tritt zurück.
/// Im kompakten Modus ist das Band dünn und ohne Skala und dient als reine Anzeige der Zusammensetzung.
/// </summary>
public sealed class MeasureBar : ThemeAwareElement
{
    private const double FullBarHeight = 28;
    private const double CompactBarHeight = 12;
    private const double TickLength = 5;
    private const double SegmentGap = 1;
    private const double HoverDimmedOpacity = 0.35;
    private const double SelectionDimmedOpacity = 0.6;

    private static readonly double[] TickSteps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 };

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<BarSegment>), typeof(MeasureBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gesamtlänge des Bands in Byte (Laufwerksgröße oder Summe des Scans).</summary>
    public static readonly DependencyProperty TotalBytesProperty = DependencyProperty.Register(
        nameof(TotalBytes), typeof(long), typeof(MeasureBar),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Scan-Fortschritt 0..1; negativ = kein Fortschritt anzeigen.</summary>
    public static readonly DependencyProperty ScanFractionProperty = DependencyProperty.Register(
        nameof(ScanFraction), typeof(double), typeof(MeasureBar),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Dünnes Band ohne Skala und ohne Interaktion.</summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(MeasureBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoveredCategoryProperty = DependencyProperty.Register(
        nameof(HoveredCategory), typeof(FileCategory?), typeof(MeasureBar),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectedCategoryProperty = DependencyProperty.Register(
        nameof(SelectedCategory), typeof(FileCategory?), typeof(MeasureBar),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public IReadOnlyList<BarSegment>? Segments
    {
        get => (IReadOnlyList<BarSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long TotalBytes
    {
        get => (long)GetValue(TotalBytesProperty);
        set => SetValue(TotalBytesProperty, value);
    }

    public double ScanFraction
    {
        get => (double)GetValue(ScanFractionProperty);
        set => SetValue(ScanFractionProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public FileCategory? HoveredCategory
    {
        get => (FileCategory?)GetValue(HoveredCategoryProperty);
        set => SetValue(HoveredCategoryProperty, value);
    }

    public FileCategory? SelectedCategory
    {
        get => (FileCategory?)GetValue(SelectedCategoryProperty);
        set => SetValue(SelectedCategoryProperty, value);
    }

    private double BarHeight => Compact ? CompactBarHeight : FullBarHeight;

    private double CornerRadius => Compact ? 4 : 6;

    private double TickTop => BarHeight + 4;

    private double LabelTop => TickTop + TickLength + 2;

    private double TotalHeight => Compact ? BarHeight : LabelTop + 16;

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, TotalHeight);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var trackBrush = (Brush)FindResource("SurfaceRaisedBrush");
        var accentBrush = (Brush)FindResource("AccentBrush");
        var tickBrush = (Brush)FindResource("TextDimBrush");

        double barHeight = BarHeight;
        var barRect = new Rect(0, 0, width, barHeight);
        dc.DrawRoundedRectangle(trackBrush, null, barRect, CornerRadius, CornerRadius);

        FileCategory? focus = Compact ? null : HoveredCategory ?? SelectedCategory;
        dc.PushClip(new RectangleGeometry(barRect, CornerRadius, CornerRadius));

        IReadOnlyList<BarSegment> segments = Segments ?? Array.Empty<BarSegment>();
        if (segments.Count == 0 && ScanFraction >= 0)
        {
            dc.PushOpacity(0.85);
            dc.DrawRectangle(accentBrush, null, new Rect(0, 0, width * Math.Min(1, ScanFraction), barHeight));
            dc.Pop();
        }

        foreach ((BarSegment segment, double x, double w) in Layout(segments, width))
        {
            var rect = new Rect(x, 0, w, barHeight);
            double dimmed = HoveredCategory is not null ? HoverDimmedOpacity : SelectionDimmedOpacity;
            double opacity = focus is not null && segment.Category != focus ? dimmed : 1;
            dc.PushOpacity(opacity);
            if (segment.Hatched)
            {
                DrawHatched(dc, segment.Fill, rect, barHeight);
            }
            else
            {
                dc.DrawRectangle(segment.Fill, null, rect);
            }

            dc.Pop();

            if (!Compact && segment.Category is not null && segment.Category == SelectedCategory)
            {
                dc.DrawRectangle(null, new Pen(accentBrush, 2), Rect.Inflate(rect, -1, -1));
            }
        }

        dc.Pop();
        if (!Compact)
        {
            DrawScale(dc, width, tickBrush);
        }
    }

    private static void DrawHatched(DrawingContext dc, Brush fill, Rect rect, double barHeight)
    {
        dc.PushOpacity(0.25);
        dc.DrawRectangle(fill, null, rect);
        dc.Pop();

        var pen = new Pen(fill, 1.2);
        dc.PushClip(new RectangleGeometry(rect));
        for (double x = rect.Left - barHeight; x < rect.Right; x += 6)
        {
            dc.DrawLine(pen, new Point(x, barHeight), new Point(x + barHeight, 0));
        }

        dc.Pop();
    }

    private IEnumerable<(BarSegment Segment, double X, double Width)> Layout(IReadOnlyList<BarSegment> segments, double width)
    {
        long total = TotalBytes;
        if (total <= 0)
        {
            yield break;
        }

        long cumulative = 0;
        foreach (BarSegment segment in segments)
        {
            double x = (double)cumulative / total * width;
            cumulative += segment.Bytes;
            double end = (double)cumulative / total * width;
            double w = end - x - SegmentGap;
            if (w >= 0.5)
            {
                yield return (segment, x, w);
            }
        }
    }

    private void DrawScale(DrawingContext dc, double width, Brush brush)
    {
        long total = TotalBytes;
        if (total <= 0)
        {
            return;
        }

        (double unitBytes, string unitName) = ChooseUnit(total);
        double totalInUnit = total / unitBytes;
        double step = TickSteps.FirstOrDefault(s => totalInUnit / s <= 10, TickSteps[^1]);

        double pixelsPerStep = step * unitBytes / total * width;
        int labelEvery = Math.Max(1, (int)Math.Ceiling(56 / pixelsPerStep));
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var pen = new Pen(brush, 1);

        for (int k = 1; k * step <= totalInUnit; k++)
        {
            double x = Math.Round(k * step * unitBytes / total * width) + 0.5;
            dc.DrawLine(pen, new Point(x, TickTop), new Point(x, TickTop + TickLength));

            if (k % labelEvery != 0)
            {
                continue;
            }

            var text = new FormattedText(
                $"{k * step:0.##} {unitName}",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                11,
                brush,
                pixelsPerDip);
            double left = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, width - text.Width));
            dc.DrawText(text, new Point(left, LabelTop));
        }
    }

    private static (double UnitBytes, string UnitName) ChooseUnit(long total)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        const double gib = mib * 1024;
        const double tib = gib * 1024;

        if (total >= 4 * tib)
        {
            return (tib, "TB");
        }

        if (total >= 4 * gib)
        {
            return (gib, "GB");
        }

        if (total >= 4 * mib)
        {
            return (mib, "MB");
        }

        return (kib, "KB");
    }

    private FileCategory? CategoryAt(double x)
    {
        IReadOnlyList<BarSegment> segments = Segments ?? Array.Empty<BarSegment>();
        foreach ((BarSegment segment, double left, double w) in Layout(segments, ActualWidth))
        {
            if (x >= left && x < left + w + SegmentGap)
            {
                return segment.Category;
            }
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Compact)
        {
            return;
        }

        Point position = e.GetPosition(this);
        FileCategory? hit = position.Y <= BarHeight ? CategoryAt(position.X) : null;
        SetCurrentValue(HoveredCategoryProperty, hit);
        Cursor = hit is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (Compact)
        {
            return;
        }

        SetCurrentValue(HoveredCategoryProperty, null);
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Compact)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (position.Y <= BarHeight && CategoryAt(position.X) is FileCategory hit)
        {
            SetCurrentValue(SelectedCategoryProperty, hit);
        }
    }
}
