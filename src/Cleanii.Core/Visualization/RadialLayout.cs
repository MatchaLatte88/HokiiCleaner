namespace Cleanii.Core.Visualization;

/// <summary>Ein Kreisabschnitt in Bogenmaß. <paramref name="Sweep"/> ist die Breite, immer größer oder gleich 0.</summary>
public readonly record struct ArcSpan(double Start, double Sweep)
{
    public double End => Start + Sweep;
}

/// <summary>Verteilt Werte als Abschnitte eines Rings. Der volle Kreis entspricht <c>total</c>.</summary>
public static class RadialLayout
{
    /// <summary>Beginn oben (12 Uhr), im Uhrzeigersinn; in Bildschirmkoordinaten mit nach unten wachsendem y.</summary>
    public const double TopStart = -Math.PI / 2;

    /// <summary>
    /// Liefert je Wert einen Abschnitt. Die Abschnitte schließen lückenlos aneinander an; eine optische Lücke
    /// zieht der Aufrufer beim Zeichnen ab. Ist die Summe der Werte größer als <paramref name="total"/>, gilt die Summe als Gesamtmenge.
    /// </summary>
    public static ArcSpan[] Layout(IReadOnlyList<long> values, long total, double startAngle = TopStart, double fullSweep = 2 * Math.PI)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (fullSweep <= 0 || fullSweep > 2 * Math.PI + 1e-9)
        {
            throw new ArgumentOutOfRangeException(nameof(fullSweep), "Der Bogen muss zwischen 0 und einem vollen Kreis liegen.");
        }

        long sum = 0;
        foreach (long value in values)
        {
            if (value < 0)
            {
                throw new ArgumentException("Werte dürfen nicht negativ sein.", nameof(values));
            }

            sum += value;
        }

        long basis = Math.Max(total, sum);
        var spans = new ArcSpan[values.Count];
        if (basis <= 0)
        {
            Array.Fill(spans, new ArcSpan(startAngle, 0));
            return spans;
        }

        long cumulative = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double start = startAngle + (double)cumulative / basis * fullSweep;
            cumulative += values[i];
            double end = startAngle + (double)cumulative / basis * fullSweep;
            spans[i] = new ArcSpan(start, end - start);
        }

        return spans;
    }

    /// <summary>Index des Abschnitts, der den Winkel enthält, oder -1. Leere Abschnitte werden nie getroffen.</summary>
    public static int IndexAt(IReadOnlyList<ArcSpan> spans, double angle, double startAngle = TopStart)
    {
        ArgumentNullException.ThrowIfNull(spans);

        double normalized = angle - startAngle;
        double full = 2 * Math.PI;
        normalized -= Math.Floor(normalized / full) * full;
        double target = startAngle + normalized;

        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i].Sweep > 0 && target >= spans[i].Start && target < spans[i].End)
            {
                return i;
            }
        }

        return -1;
    }
}
