using System.Globalization;

namespace TattleTail.Core;

/// <summary>Turns byte counts into human-friendly text like "1.5 MB" or "320 KB/s".</summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(double bytes)
    {
        if (bytes < 0 || double.IsNaN(bytes))
        {
            bytes = 0;
        }

        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        var format = unit == 0 || bytes >= 100 ? "0" : "0.0";
        return bytes.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond < 1 ? string.Empty : Format(bytesPerSecond) + "/s";
}
