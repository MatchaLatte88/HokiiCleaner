using HokiiCleaner.Core.Visualization;

namespace HokiiCleaner.Tests;

public class SquarifiedTreemapTests
{
    private static readonly LayoutRect Bounds = new(10, 20, 800, 500);
    private const double Tolerance = 1e-6;

    private static double Overlap(LayoutRect a, LayoutRect b)
    {
        double w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    [Theory]
    [InlineData(new double[] { 1 })]
    [InlineData(new double[] { 6, 6, 4, 3, 2, 2, 1 })]
    [InlineData(new double[] { 100, 50, 25, 12, 6, 3, 1, 1, 1, 1 })]
    [InlineData(new double[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 })]
    [InlineData(new double[] { 1000, 1, 1, 1 })]
    public void Rectangles_tile_the_bounds_with_areas_proportional_to_weights(double[] weights)
    {
        LayoutRect[] rects = SquarifiedTreemap.Layout(weights, Bounds);

        Assert.Equal(weights.Length, rects.Length);
        double totalWeight = weights.Sum();
        for (int i = 0; i < rects.Length; i++)
        {
            double expected = Bounds.Area * weights[i] / totalWeight;
            Assert.InRange(rects[i].Area, expected * (1 - 1e-6), expected * (1 + 1e-6));

            Assert.True(rects[i].X >= Bounds.X - Tolerance && rects[i].Y >= Bounds.Y - Tolerance);
            Assert.True(rects[i].X + rects[i].Width <= Bounds.X + Bounds.Width + Tolerance);
            Assert.True(rects[i].Y + rects[i].Height <= Bounds.Y + Bounds.Height + Tolerance);
        }

        Assert.Equal(Bounds.Area, rects.Sum(r => r.Area), 3);

        for (int i = 0; i < rects.Length; i++)
        {
            for (int j = i + 1; j < rects.Length; j++)
            {
                Assert.True(Overlap(rects[i], rects[j]) < 1e-3, $"Rechtecke {i} und {j} überlappen.");
            }
        }
    }

    [Fact]
    public void Single_weight_fills_the_bounds()
    {
        LayoutRect only = Assert.Single(SquarifiedTreemap.Layout(new double[] { 42 }, Bounds));

        Assert.Equal(Bounds.X, only.X, 6);
        Assert.Equal(Bounds.Y, only.Y, 6);
        Assert.Equal(Bounds.Width, only.Width, 6);
        Assert.Equal(Bounds.Height, only.Height, 6);
    }

    [Fact]
    public void Layout_prefers_squarish_rectangles()
    {
        LayoutRect[] rects = SquarifiedTreemap.Layout(new double[] { 6, 6, 4, 3, 2, 2, 1 }, new LayoutRect(0, 0, 600, 400));

        foreach (LayoutRect rect in rects)
        {
            double ratio = Math.Max(rect.Width / rect.Height, rect.Height / rect.Width);
            Assert.True(ratio < 3.5, $"Seitenverhältnis {ratio:N2} zu extrem.");
        }
    }

    [Fact]
    public void Non_positive_weights_get_empty_rectangles_and_do_not_take_space()
    {
        LayoutRect[] rects = SquarifiedTreemap.Layout(new double[] { 10, 0, 5, -3 }, Bounds);

        Assert.Equal(default, rects[1]);
        Assert.Equal(default, rects[3]);
        Assert.Equal(Bounds.Area, rects[0].Area + rects[2].Area, 3);
    }

    [Fact]
    public void Empty_input_or_degenerate_bounds_give_empty_result()
    {
        Assert.Empty(SquarifiedTreemap.Layout(Array.Empty<double>(), Bounds));
        Assert.All(SquarifiedTreemap.Layout(new double[] { 1, 2 }, new LayoutRect(0, 0, 0, 100)), r => Assert.Equal(default, r));
        Assert.All(SquarifiedTreemap.Layout(new double[] { 0, 0 }, Bounds), r => Assert.Equal(default, r));
    }

    [Fact]
    public void Non_finite_weight_throws()
    {
        Assert.Throws<ArgumentException>(() => SquarifiedTreemap.Layout(new[] { 1.0, double.NaN }, Bounds));
        Assert.Throws<ArgumentException>(() => SquarifiedTreemap.Layout(new[] { 1.0, double.PositiveInfinity }, Bounds));
    }
}
