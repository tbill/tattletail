using TattleTail.Core;
using Xunit;

namespace TattleTail.Core.Tests;

public class ByteFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(150 * 1024, "150 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    [InlineData(-5, "0 B")]
    public void Format(double bytes, string expected) => Assert.Equal(expected, ByteFormatter.Format(bytes));

    [Fact]
    public void FormatRate_IsBlankWhenIdle() => Assert.Equal(string.Empty, ByteFormatter.FormatRate(0.5));

    [Fact]
    public void FormatRate_AddsPerSecond() => Assert.Equal("2.0 KB/s", ByteFormatter.FormatRate(2048));
}
