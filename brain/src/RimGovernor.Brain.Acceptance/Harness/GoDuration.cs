using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Go duration text (Go <c>time.Duration.String</c> / <c>ParseDuration</c>), for specs and labels.</summary>
public static partial class GoDuration
{
    public static bool TryParse(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        var s = text;
        var negative = false;
        if (s.Length > 0 && (s[0] == '+' || s[0] == '-'))
        {
            negative = s[0] == '-';
            s = s[1..];
        }

        if (s == "0")
        {
            // Go's ParseDuration takes a bare zero without a unit.
            return true;
        }

        if (s.Length == 0 || !FullPattern().IsMatch(s))
        {
            return false;
        }

        double ticks = 0;
        foreach (var m in PartPattern().EnumerateMatches(s))
        {
            var part = s.AsSpan(m.Index, m.Length);
            var split = part.IndexOfAnyExceptInRange('0', '9');
            while (split >= 0 && part[split] == '.')
            {
                var next = part[(split + 1)..].IndexOfAnyExceptInRange('0', '9');
                split = next < 0 ? -1 : split + 1 + next;
            }

            var number = double.Parse(part[..split], NumberStyles.Float, CultureInfo.InvariantCulture);
            var unit = part[split..] switch
            {
                "ns" => TimeSpan.TicksPerMillisecond / 1_000_000.0,
                "us" or "µs" => TimeSpan.TicksPerMillisecond / 1000.0,
                "ms" => TimeSpan.TicksPerMillisecond,
                "s" => TimeSpan.TicksPerSecond,
                "m" => TimeSpan.TicksPerMinute,
                _ => (double)TimeSpan.TicksPerHour,
            };
            ticks += number * unit;
        }

        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            return false;
        }

        value = TimeSpan.FromTicks((long)(negative ? -ticks : ticks));
        return true;
    }

    /// <summary>Formats like Go: "1m30s", "45s", "1h0m0s", "500ms", "1.5s".</summary>
    public static string Format(TimeSpan d)
    {
        if (d == TimeSpan.Zero)
        {
            return "0s";
        }

        var ticks = Math.Abs(d.Ticks);
        var b = new StringBuilder();
        if (d < TimeSpan.Zero)
        {
            b.Append('-');
        }

        if (ticks < TimeSpan.TicksPerSecond)
        {
            b.Append(Fraction(ticks, TimeSpan.TicksPerMillisecond)).Append("ms");
            return b.ToString();
        }

        var hours = ticks / TimeSpan.TicksPerHour;
        var minutes = ticks % TimeSpan.TicksPerHour / TimeSpan.TicksPerMinute;
        var secondTicks = ticks % TimeSpan.TicksPerMinute;
        if (hours > 0)
        {
            b.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('h');
        }

        if (hours > 0 || minutes > 0)
        {
            b.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('m');
        }

        b.Append(Fraction(secondTicks, TimeSpan.TicksPerSecond)).Append('s');
        return b.ToString();
    }

    private static string Fraction(long ticks, long unit)
    {
        var whole = ticks / unit;
        var rest = ticks % unit;
        if (rest == 0)
        {
            return whole.ToString(CultureInfo.InvariantCulture);
        }

        var digits = unit.ToString(CultureInfo.InvariantCulture).Length - 1;
        var fraction = rest.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0').TrimEnd('0');
        return whole.ToString(CultureInfo.InvariantCulture) + "." + fraction;
    }

    [GeneratedRegex(@"^(?:(?:\d+(?:\.\d*)?|\.\d+)(?:ns|us|µs|ms|s|m|h))+$")]
    private static partial Regex FullPattern();

    [GeneratedRegex(@"(?:\d+(?:\.\d*)?|\.\d+)(?:ns|us|µs|ms|s|m|h)")]
    private static partial Regex PartPattern();
}
