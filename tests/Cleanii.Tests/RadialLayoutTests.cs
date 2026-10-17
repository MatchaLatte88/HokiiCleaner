using Cleanii.Core.Visualization;

namespace Cleanii.Tests;

public class RadialLayoutTests
{
    private const double Tau = 2 * Math.PI;

    [Fact]
    public void Spans_start_at_twelve_oclock_and_follow_each_other_without_gaps()
    {
        ArcSpan[] spans = RadialLayout.Layout(new long[] { 25, 25, 50 }, 100);

        Assert.Equal(RadialLayout.TopStart, spans[0].Start, 9);
        Assert.Equal(Tau / 4, spans[0].Sweep, 9);
        Assert.Equal(Tau / 4, spans[1].Sweep, 9);
        Assert.Equal(Tau / 2, spans[2].Sweep, 9);
        for (int i = 1; i < spans.Length; i++)
        {
            Assert.Equal(spans[i - 1].End, spans[i].Start, 9);
        }

        Assert.Equal(RadialLayout.TopStart + Tau, spans[^1].End, 9);
    }

    [Fact]
    public void Values_below_the_total_leave_the_rest_of_the_circle_free()
    {
        ArcSpan[] spans = RadialLayout.Layout(new long[] { 100, 200 }, 1000);

        Assert.Equal(Tau * 0.1, spans[0].Sweep, 9);
        Assert.Equal(Tau * 0.2, spans[1].Sweep, 9);
        Assert.True(spans[1].End < RadialLayout.TopStart + Tau);
    }

    [Fact]
    public void A_sum_above_the_total_is_scaled_to_the_full_circle_instead_of_overflowing()
    {
        ArcSpan[] spans = RadialLayout.Layout(new long[] { 600, 600 }, 1000);

        Assert.Equal(RadialLayout.TopStart + Tau, spans[^1].End, 9);
        Assert.Equal(Tau / 2, spans[0].Sweep, 9);
    }

    [Fact]
    public void Zero_total_gives_empty_spans_and_zero_values_give_empty_spans()
    {
        Assert.All(RadialLayout.Layout(new long[] { 0, 0 }, 0), s => Assert.Equal(0, s.Sweep));

        ArcSpan[] spans = RadialLayout.Layout(new long[] { 0, 50 }, 100);
        Assert.Equal(0, spans[0].Sweep);
        Assert.Equal(Tau / 2, spans[1].Sweep, 9);
    }

    [Fact]
    public void A_partial_arc_can_be_requested()
    {
        ArcSpan[] spans = RadialLayout.Layout(new long[] { 1, 1 }, 2, startAngle: 0, fullSweep: Math.PI);

        Assert.Equal(0, spans[0].Start, 9);
        Assert.Equal(Math.PI / 2, spans[0].Sweep, 9);
        Assert.Equal(Math.PI, spans[1].End, 9);
    }

    [Fact]
    public void Invalid_input_throws()
    {
        Assert.Throws<ArgumentException>(() => RadialLayout.Layout(new long[] { 1, -1 }, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => RadialLayout.Layout(new long[] { 1 }, 10, fullSweep: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RadialLayout.Layout(new long[] { 1 }, 10, fullSweep: 7));
    }

    [Theory]
    [InlineData(-Math.PI / 2 + 0.1, 0)]          // knapp rechts von 12 Uhr: erster Abschnitt
    [InlineData(0.1, 1)]                           // 3 Uhr: zweiter Abschnitt
    [InlineData(Math.PI / 2 + 0.1, 2)]             // 6 Uhr: dritter Abschnitt
    [InlineData(Math.PI + 0.1, 2)]                 // 9 Uhr: dritter Abschnitt
    [InlineData(-Math.PI / 2 - 0.1, 2)]            // knapp links von 12 Uhr: Ende des Kreises
    public void IndexAt_finds_the_span_containing_an_angle(double angle, int expected)
    {
        ArcSpan[] spans = RadialLayout.Layout(new long[] { 25, 25, 50 }, 100);

        Assert.Equal(expected, RadialLayout.IndexAt(spans, angle));
    }

    [Fact]
    public void IndexAt_wraps_angles_and_ignores_free_space_and_empty_spans()
    {
        ArcSpan[] spans = RadialLayout.Layout(new long[] { 0, 10 }, 100); // nur 10 % belegt, ab 12 Uhr

        Assert.Equal(-1, RadialLayout.IndexAt(spans, RadialLayout.TopStart + Tau * 0.5)); // freier Bereich
        Assert.Equal(1, RadialLayout.IndexAt(spans, RadialLayout.TopStart + Tau * 0.05));
        Assert.Equal(1, RadialLayout.IndexAt(spans, RadialLayout.TopStart + Tau * 0.05 + Tau * 3)); // volle Umdrehungen dazu
        Assert.Equal(1, RadialLayout.IndexAt(spans, RadialLayout.TopStart + Tau * 0.05 - Tau));
    }
}
