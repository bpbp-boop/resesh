using System.Globalization;
using Resesh.Core.History;

namespace Resesh.Core.Tests;

public sealed class ElapsedTextTests
{
    [Theory]
    [InlineData(0.42, "420 ms")]
    [InlineData(0.9994, "999 ms")]
    [InlineData(0.9996, "1 s")]
    [InlineData(12.54, "12.5 s")]
    [InlineData(59.94, "59.9 s")]
    [InlineData(59.96, "1 min 0 s")]
    [InlineData(184, "3 min 4 s")]
    [InlineData(3599.4, "59 min 59 s")]
    [InlineData(3599.6, "1 h 0 min")]
    [InlineData(7500, "2 h 5 min")]
    public void ShortRoundsBeforeChoosingTheUnit(double seconds, string text)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(text, ElapsedText.Short(TimeSpan.FromSeconds(seconds)));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData(-1, "00:00.000")]
    [InlineData(83.25, "01:23.250")]
    [InlineData(3600, "01:00:00.000")]
    [InlineData(25 * 3600 + 61.5, "25:01:01.500")]
    public void ClockCountsHoursInFull(double seconds, string text) =>
        Assert.Equal(text, ElapsedText.Clock(TimeSpan.FromSeconds(seconds)));
}
