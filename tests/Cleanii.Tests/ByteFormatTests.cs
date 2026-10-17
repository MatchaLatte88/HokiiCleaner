using System.Globalization;
using Cleanii.Core;

namespace Cleanii.Tests;

public class ByteFormatTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1_048_576L, "1 MB")]
    [InlineData(1_073_741_824L, "1.00 GB")]
    [InlineData(1_099_511_627_776L, "1.00 TB")]
    public void Format_uses_binary_units_with_fewer_decimals_for_small_units(long bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Format(bytes, En));
    }

    [Fact]
    public void Split_returns_number_and_unit_separately_and_respects_the_culture()
    {
        (string number, string unit) = ByteFormat.Split(860_063_858_688, De);

        Assert.Equal("801,00", number); // 800,997 GiB
        Assert.Equal("GB", unit);
        Assert.Equal("801,00 GB", ByteFormat.Format(860_063_858_688, De));
    }

    [Fact]
    public void Split_handles_negative_values_by_magnitude()
    {
        (string number, string unit) = ByteFormat.Split(-2_097_152, En);

        Assert.Equal("-2", number);
        Assert.Equal("MB", unit);
    }
}
