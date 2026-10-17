using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HokiiCleaner.Core.Categorization;
using HokiiCleaner.Core.Visualization;

namespace HokiiCleaner.App.Controls;

/// <summary>Anteil eines Typs an einem Ordner (äußerer Ring).</summary>
public sealed record DialPart(FileCategory Category, long Bytes, Brush Fill);

/// <summary>Ein Ordner im inneren Ring. <paramref name="Key"/> ist null bei Sammelabschnitten, die nicht wählbar sind.</summary>
public sealed record DialSegment(object? Key, long Bytes, Brush Fill, IReadOnlyList<DialPart> Parts);

/// <summary>
/// Sonar: Ein Ordner oder Laufwerk als Ring. Die Fläche eines Abschnitts ist sein Anteil an der Gesamtmenge,
/// ein äußerer Ring zeigt die Typen. Freier Platz bleibt als leere Spur sichtbar.
/// </summary>
public sealed class SonarDial : ThemeAwareElement
{
    // Maße im Raum 560 x 560, werden auf die tatsächliche Größe skaliert.
    private const double ModelSize = 560;
    private const double InnerRadius = 142;
    private const double FolderOuterRadius = 206;
    private const double TypeInnerRadius = 214;
    private const double TypeOuterRadius = 240;
    private const double GapAngle = 0.006;
    private const double DimmedOpacity = 0.16;

    private static readonly double[] TickSteps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 };

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<DialSegment>), typeof(SonarDial),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentsChanged));

    /// <summary>Gesamtmenge des Rings in Byte (Laufwerksgröße oder Ordnergröße).</summary>
    public static readonly DependencyProperty TotalBytesProperty = DependencyProperty.Register(
        nameof(TotalBytes), typeof(long), typeof(SonarDial),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Belegter, aber keinem Ordner zugeordneter Platz; wird schraffiert gezeichnet.</summary>
    public static readonly DependencyProperty UnattributedBytesProperty = DependencyProperty.Register(
        nameof(UnattributedBytes), typeof(long), typeof(SonarDial),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Scan-Fortschritt 0..1; negativ = nicht anzeigen.</summary>
    public static readonly DependencyProperty ScanFractionProperty = DependencyProperty.Register(
        nameof(ScanFraction), typeof(double), typeof(SonarDial),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(object), typeof(SonarDial),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HoveredKeyProperty = DependencyProperty.Register(
        nameof(HoveredKey), typeof(object), typeof(SonarDial),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Typ, der hervorgehoben wird; alles andere tritt zurück. <c>null</c> = keine Hervorhebung.</summary>
    public static readonly DependencyProperty FocusCategoryProperty = DependencyProperty.Register(
        nameof(FocusCategory), typeof(FileCategory?), typeof(SonarDial),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Aufbau-Animation 0..1: Die Abschnitte wachsen im Uhrzeigersinn ein.</summary>
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(SonarDial),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand), typeof(ICommand), typeof(SonarDial), new PropertyMetadata(null));

    private (double Scale, Point Center) _geometry;
    private ArcSpan[] _spans = Array.Empty<ArcSpan>();
    private IReadOnlyList<DialSegment> _spanSegments = Array.Empty<DialSegment>();

    public IReadOnlyList<DialSegment>? Segments
    {
        get => (IReadOnlyList<DialSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long TotalBytes
    {
        get => (long)GetValue(TotalBytesProperty);
        set => SetValue(TotalBytesProperty, value);
    }

    public long UnattributedBytes
    {
        get => (long)GetValue(UnattributedBytesProperty);
        set => SetValue(UnattributedBytesProperty, value);
    }

    public double ScanFraction
    {
        get => (double)GetValue(ScanFractionProperty);
        set => SetValue(ScanFractionProperty, value);
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

    public FileCategory? FocusCategory
    {
        get => (FileCategory?)GetValue(FocusCategoryProperty);
        set => SetValue(FocusCategoryProperty, value);
    }

    public double Reveal
    {
        get => (double)GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    public ICommand? ActivateCommand
    {
        get => (ICommand?)GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dial = (SonarDial)d;
        var segments = e.NewValue as IReadOnlyList<DialSegment>;
        if (segments is null || segments.Count == 0)
        {
            dial.BeginAnimation(RevealProperty, null);
            dial.Reveal = 1;
            return;
        }

        // Neue Daten wachsen ein; BeginAnimation überschreibt den lokalen Wert nur während der Animation.
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        dial.Reveal = 1;
        dial.BeginAnimation(RevealProperty, animation);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(
            double.IsInfinity(availableSize.Width) ? 520 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 520 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        double scale = size / ModelSize;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        _geometry = (scale, center);

        var trackBrush = (Brush)FindResource("TrackBrush");
        var tickBrush = (Brush)FindResource("TickBrush");
        var tickMajorBrush = (Brush)FindResource("TickMajorBrush");
        var dimBrush = (Brush)FindResource("TextDimBrush");
        var accentBrush = (Brush)FindResource("AccentBrush");

        // Spur über den ganzen Kreis; freier Platz bleibt als leere Spur sichtbar
        var track = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new EllipseGeometry(center, FolderOuterRadius * scale, FolderOuterRadius * scale),
            new EllipseGeometry(center, InnerRadius * scale, InnerRadius * scale));
        dc.DrawGeometry(trackBrush, new Pen(tickBrush, 1), track);

        DrawScale(dc, center, scale, tickBrush, tickMajorBrush, dimBrush);

        IReadOnlyList<DialSegment> segments = Segments ?? Array.Empty<DialSegment>();
        long total = Math.Max(1, TotalBytes);
        var values = new List<long>(segments.Count + 1);
        foreach (DialSegment segment in segments)
        {
            values.Add(segment.Bytes);
        }

        values.Add(UnattributedBytes);
        ArcSpan[] spans = RadialLayout.Layout(values, total);
        _spans = spans;
        _spanSegments = segments;

        double limit = RadialLayout.TopStart + 2 * Math.PI * Math.Clamp(Reveal, 0, 1);
        FileCategory? focus = FocusCategory;
        object? hovered = HoveredKey;
        object? selected = SelectedKey;

        if (segments.Count == 0 && ScanFraction >= 0)
        {
            double end = RadialLayout.TopStart + 2 * Math.PI * Math.Min(1, ScanFraction);
            dc.PushOpacity(0.9);
            DrawArc(dc, accentBrush, null, center, scale, InnerRadius, FolderOuterRadius, RadialLayout.TopStart, end);
            dc.Pop();
        }

        for (int i = 0; i < segments.Count; i++)
        {
            DialSegment segment = segments[i];
            ArcSpan span = spans[i];
            if (span.Sweep <= 0 || span.Start >= limit)
            {
                continue;
            }

            double start = span.Start + GapAngle / 2;
            double end = Math.Min(span.End, limit) - GapAngle / 2;
            bool matchesFocus = focus is null || segment.Parts.Any(p => p.Category == focus);

            dc.PushOpacity(matchesFocus ? 1 : DimmedOpacity);
            DrawArc(dc, segment.Fill, null, center, scale, InnerRadius, FolderOuterRadius, start, end);
            dc.Pop();

            if (segment.Key is not null && Equals(segment.Key, hovered))
            {
                DrawArc(dc, new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), null, center, scale, InnerRadius, FolderOuterRadius, start, end);
            }

            // Typen innerhalb des Ordners
            long partTotal = segment.Parts.Sum(p => p.Bytes);
            if (partTotal > 0)
            {
                double inner = span.Start;
                foreach (DialPart part in segment.Parts)
                {
                    double partSweep = span.Sweep * part.Bytes / partTotal;
                    double partStart = inner + GapAngle / 2;
                    double partEnd = Math.Min(inner + partSweep, limit) - GapAngle / 2;
                    inner += partSweep;
                    if (partStart >= limit || partEnd <= partStart)
                    {
                        continue;
                    }

                    dc.PushOpacity(focus is null || part.Category == focus ? 0.95 : 0.12);
                    DrawArc(dc, part.Fill, null, center, scale, TypeInnerRadius, TypeOuterRadius, partStart, partEnd);
                    dc.Pop();
                }
            }

            if (segment.Key is not null && Equals(segment.Key, selected))
            {
                DrawArc(dc, null, new Pen(accentBrush, 2.5 * Math.Max(1, scale)), center, scale, InnerRadius, FolderOuterRadius, start, end);
            }
        }

        // Nicht zugeordneter Platz (schraffiert)
        ArcSpan unattributed = spans[^1];
        if (unattributed.Sweep > 0 && unattributed.Start < limit)
        {
            double end = Math.Min(unattributed.End, limit) - GapAngle / 2;
            DrawArc(dc, CreateHatchBrush(dimBrush), new Pen(dimBrush, 1), center, scale, InnerRadius, FolderOuterRadius, unattributed.Start + GapAngle / 2, end);
        }

        // innere gestrichelte Linie
        var dashed = new Pen(tickBrush, 1) { DashStyle = new DashStyle(new double[] { 2, 5 }, 0) };
        dc.DrawEllipse(null, dashed, center, (InnerRadius - 10) * scale, (InnerRadius - 10) * scale);
    }

    private static void DrawArc(DrawingContext dc, Brush? fill, Pen? pen, Point center, double scale, double r0, double r1, double a0, double a1)
    {
        if (a1 - a0 <= 0 || (a1 - a0) * r1 * scale < 0.6)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            Point p0 = Polar(center, r1 * scale, a0), p1 = Polar(center, r1 * scale, a1);
            Point p2 = Polar(center, r0 * scale, a1), p3 = Polar(center, r0 * scale, a0);
            bool large = a1 - a0 > Math.PI;
            ctx.BeginFigure(p0, true, true);
            ctx.ArcTo(p1, new Size(r1 * scale, r1 * scale), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(p2, true, false);
            ctx.ArcTo(p3, new Size(r0 * scale, r0 * scale), 0, large, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(fill, pen, geometry);
    }

    private static Point Polar(Point center, double radius, double angle)
        => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    private static Brush CreateHatchBrush(Brush lineBrush)
    {
        var drawing = new DrawingGroup();
        using (DrawingContext ctx = drawing.Open())
        {
            ctx.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 6, 6));
            ctx.DrawRectangle(lineBrush, null, new Rect(0, 0, 2, 6));
        }

        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Transform = new RotateTransform(45),
            Opacity = 0.55,
        };
    }

    private void DrawScale(DrawingContext dc, Point center, double scale, Brush tickBrush, Brush majorBrush, Brush labelBrush)
    {
        long total = TotalBytes;
        if (total <= 0)
        {
            return;
        }

        (double unitBytes, _) = ChooseUnit(total);
        double totalInUnit = total / unitBytes;
        double step = TickSteps.FirstOrDefault(s => totalInUnit / s <= 100, TickSteps[^1]);
        double majorEvery = step * 10;

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var minorPen = new Pen(tickBrush, 1);
        var majorPen = new Pen(majorBrush, 1.4);
        double reveal = Math.Clamp(Reveal, 0, 1);

        for (double v = 0; v <= totalInUnit + 1e-9; v += step)
        {
            double angle = RadialLayout.TopStart + v / totalInUnit * 2 * Math.PI;
            bool major = Math.Abs(v / majorEvery - Math.Round(v / majorEvery)) < 1e-9;
            Point a = Polar(center, (TypeOuterRadius + 6) * scale, angle);
            Point b = Polar(center, (TypeOuterRadius + (major ? 15 : 10)) * scale, angle);
            dc.DrawLine(major ? majorPen : minorPen, a, b);

            if (major && v > 0 && v < totalInUnit - majorEvery * 0.2 && reveal >= 0.999)
            {
                var text = new FormattedText(
                    v.ToString("0.##", CultureInfo.CurrentCulture),
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    Math.Max(9, 11 * scale),
                    labelBrush,
                    pixelsPerDip);
                Point p = Polar(center, (TypeOuterRadius + 29) * scale, angle);
                dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
            }
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

    /// <summary>Index des Ordners unter dem Mauszeiger (innerer oder äußerer Ring), sonst -1.</summary>
    private int SegmentAt(Point point)
    {
        (double scale, Point center) = _geometry;
        if (scale <= 0)
        {
            return -1;
        }

        double dx = point.X - center.X, dy = point.Y - center.Y;
        double radius = Math.Sqrt(dx * dx + dy * dy) / scale;
        if (radius < InnerRadius || radius > TypeOuterRadius)
        {
            return -1;
        }

        int index = RadialLayout.IndexAt(_spans, Math.Atan2(dy, dx));
        return index >= 0 && index < _spanSegments.Count ? index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = SegmentAt(e.GetPosition(this));
        object? key = index >= 0 ? _spanSegments[index].Key : null;
        SetCurrentValue(HoveredKeyProperty, key);
        Cursor = key is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetCurrentValue(HoveredKeyProperty, null);
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int index = SegmentAt(e.GetPosition(this));
        object? key = index >= 0 ? _spanSegments[index].Key : null;
        if (key is null)
        {
            return;
        }

        SetCurrentValue(SelectedKeyProperty, key);
        if (e.ClickCount == 2 && ActivateCommand?.CanExecute(key) == true)
        {
            ActivateCommand.Execute(key);
        }
    }
}
