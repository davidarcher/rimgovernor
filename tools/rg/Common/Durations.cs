using System.Globalization;
using System.Text;

namespace RimGovernor.Tools.Common;

internal static class Durations
{
    /// <summary>Parses Go-style duration text (<c>1h30m</c>, <c>90s</c>, <c>500ms</c>).</summary>
    public static bool TryParse(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        var i = 0;
        var any = false;
        double totalSeconds = 0;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
            {
                i++;
            }
            if (i == start || !double.TryParse(text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }
            var unitStart = i;
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }
            var scale = text[unitStart..i] switch
            {
                "h" => 3600.0,
                "m" => 60.0,
                "s" => 1.0,
                "ms" => 0.001,
                _ => -1.0,
            };
            if (scale < 0)
            {
                return false;
            }
            totalSeconds += n * scale;
            any = true;
        }
        if (!any)
        {
            return false;
        }
        value = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    /// <summary>Whole-second text in Go's style: <c>1h2m3s</c>, <c>2m3s</c>, <c>5s</c>.</summary>
    public static string Format(TimeSpan span)
    {
        var seconds = (long)Math.Round(span.TotalSeconds, MidpointRounding.AwayFromZero);
        var sb = new StringBuilder();
        if (seconds < 0)
        {
            sb.Append('-');
            seconds = -seconds;
        }
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        if (h > 0)
        {
            sb.Append(h).Append('h');
        }
        if (h > 0 || m > 0)
        {
            sb.Append(m).Append('m');
        }
        sb.Append(s).Append('s');
        return sb.ToString();
    }
}
