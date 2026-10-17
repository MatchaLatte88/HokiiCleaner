namespace HokiiCleaner.Core.Visualization;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

/// <summary>Treemap-Layout nach Bruls, Huizing und van Wijk (squarified): möglichst quadratische Rechtecke.</summary>
public static class SquarifiedTreemap
{
    /// <summary>
    /// Verteilt <paramref name="bounds"/> proportional zu den Gewichten. Die Gewichte sollten absteigend sortiert sein,
    /// das Ergebnis hat dieselbe Reihenfolge. Gewichte kleiner oder gleich 0 erhalten ein leeres Rechteck.
    /// </summary>
    public static LayoutRect[] Layout(IReadOnlyList<double> weights, LayoutRect bounds)
    {
        ArgumentNullException.ThrowIfNull(weights);

        var result = new LayoutRect[weights.Count];
        var indices = new List<int>(weights.Count);
        double total = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            if (double.IsNaN(weights[i]) || double.IsInfinity(weights[i]))
            {
                throw new ArgumentException($"Gewicht an Position {i} ist keine endliche Zahl.", nameof(weights));
            }

            if (weights[i] > 0)
            {
                indices.Add(i);
                total += weights[i];
            }
        }

        if (indices.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return result;
        }

        double areaPerWeight = bounds.Area / total;
        LayoutRect free = bounds;
        int position = 0;

        while (position < indices.Count)
        {
            double shortSide = Math.Min(free.Width, free.Height);

            int end = position;
            double rowArea = 0;
            double worst = double.MaxValue;
            while (end < indices.Count)
            {
                double area = weights[indices[end]] * areaPerWeight;
                double candidateArea = rowArea + area;
                double largest = weights[indices[position]] * areaPerWeight;
                double smallest = area; // absteigend sortiert: das neue Element ist das kleinste der Zeile
                double candidateWorst = Math.Max(
                    shortSide * shortSide * largest / (candidateArea * candidateArea),
                    candidateArea * candidateArea / (shortSide * shortSide * smallest));

                if (end > position && candidateWorst > worst)
                {
                    break;
                }

                rowArea = candidateArea;
                worst = candidateWorst;
                end++;
            }

            bool isLastRow = end >= indices.Count;
            if (free.Width >= free.Height)
            {
                // Senkrechter Streifen links; seine Breite ergibt sich aus der Zeilenfläche.
                double stripWidth = isLastRow ? free.Width : rowArea / free.Height;
                double y = free.Y;
                for (int k = position; k < end; k++)
                {
                    double h = isLastRow && k == end - 1
                        ? free.Y + free.Height - y
                        : weights[indices[k]] * areaPerWeight / stripWidth;
                    result[indices[k]] = new LayoutRect(free.X, y, stripWidth, h);
                    y += h;
                }

                free = new LayoutRect(free.X + stripWidth, free.Y, free.Width - stripWidth, free.Height);
            }
            else
            {
                // Waagerechter Streifen oben.
                double stripHeight = isLastRow ? free.Height : rowArea / free.Width;
                double x = free.X;
                for (int k = position; k < end; k++)
                {
                    double w = isLastRow && k == end - 1
                        ? free.X + free.Width - x
                        : weights[indices[k]] * areaPerWeight / stripHeight;
                    result[indices[k]] = new LayoutRect(x, free.Y, w, stripHeight);
                    x += w;
                }

                free = new LayoutRect(free.X, free.Y + stripHeight, free.Width, free.Height - stripHeight);
            }

            position = end;
        }

        return result;
    }
}
