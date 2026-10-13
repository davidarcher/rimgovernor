using System.Collections;
using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// The wire encoding a truncated row's size, hash and preview are taken over: Go's <c>encoding/json</c> form of
/// the normalized payload (keys in ordinal order, compact, HTML characters escaped, floats in Go's shortest
/// form). A hash recorded by the Go service and one recorded here agree for the same payload.
/// </summary>
internal static class RowJson
{
    private const string Hex = "0123456789abcdef";

    /// <summary>Encodes a normalized value (see <see cref="RowValues.Normalize"/>), or returns null when it holds a number JSON cannot carry.</summary>
    internal static byte[]? Encode(object? value)
    {
        var sb = new StringBuilder();
        return Write(sb, value) ? Encoding.UTF8.GetBytes(sb.ToString()) : null;
    }

    private static bool Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return true;
            case string s:
                WriteString(sb, s);
                return true;
            case bool b:
                sb.Append(b ? "true" : "false");
                return true;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return true;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    return false;
                }
                sb.Append(FormatDouble(d));
                return true;
            case IReadOnlyDictionary<string, object?> map:
                sb.Append('{');
                var first = true;
                foreach (var key in map.Keys.Order(StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    if (!Write(sb, map[key]))
                    {
                        return false;
                    }
                }
                sb.Append('}');
                return true;
            case IReadOnlyList<object?> list:
                sb.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    if (!Write(sb, list[i]))
                    {
                        return false;
                    }
                }
                sb.Append(']');
                return true;
            default:
                throw new InvalidOperationException("RowJson encodes only normalized values, not " + value.GetType().FullName);
        }
    }

    // encoding/json's string form: HTML characters and U+2028/2029 as \u escapes, invalid UTF-16 as U+FFFD.
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '<' or '>' or '&' or (char)0x2028 or (char)0x2029:
                    AppendUnicode(sb, c);
                    break;
                case < ' ':
                    AppendUnicode(sb, c);
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        sb.Append("\\ufffd");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    private static void AppendUnicode(StringBuilder sb, char c)
    {
        sb.Append("\\u")
            .Append(Hex[(c >> 12) & 0xf])
            .Append(Hex[(c >> 8) & 0xf])
            .Append(Hex[(c >> 4) & 0xf])
            .Append(Hex[c & 0xf]);
    }

    // strconv.AppendFloat(f, fmt, -1, 64) with encoding/json's choice of fmt: 'f' unless the magnitude is below
    // 1e-6 or at least 1e21, then 'e' with a single-digit negative exponent unpadded.
    internal static string FormatDouble(double value)
    {
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }
        var abs = Math.Abs(value);
        var (digits, decExp) = ShortestDigits(abs);
        var sb = new StringBuilder();
        if (value < 0)
        {
            sb.Append('-');
        }
        if (abs < 1e-6 || abs >= 1e21)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1)
            {
                sb.Append('.').Append(digits, 1, digits.Length - 1);
            }
            var exp = decExp - 1;
            sb.Append('e').Append(exp < 0 ? '-' : '+');
            var mag = Math.Abs(exp);
            if (exp < 0 && mag < 10)
            {
                sb.Append(mag.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(mag.ToString("00", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
        if (decExp <= 0)
        {
            sb.Append("0.").Append('0', -decExp).Append(digits);
        }
        else if (decExp >= digits.Length)
        {
            sb.Append(digits).Append('0', decExp - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, decExp).Append('.').Append(digits, decExp, digits.Length - decExp);
        }
        return sb.ToString();
    }

    // The shortest digit string that round-trips and the decimal exponent: abs = 0.DIGITS x 10^decExp.
    private static (string Digits, int DecExp) ShortestDigits(double abs)
    {
        var text = abs.ToString("R", CultureInfo.InvariantCulture);
        var exp = 0;
        var e = text.IndexOf('E', StringComparison.Ordinal);
        if (e >= 0)
        {
            exp = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var pointPos = point < 0 ? text.Length : point;
        var digits = text.Replace(".", "", StringComparison.Ordinal);
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
        }
        digits = digits[lead..];
        pointPos -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }
        return (digits, pointPos + exp);
    }
}

/// <summary>Normalizes payload values to the forms a <see cref="TelemetryRecord"/> carries.</summary>
internal static class RowValues
{
    /// <summary>
    /// Scalars as themselves (integers as long, floats as double), durations in milliseconds, exceptions and
    /// every other object as their text, sequences as arrays and string-keyed maps as kvlists.
    /// </summary>
    internal static object? Normalize(object? v)
    {
        switch (v)
        {
            case null:
                return null;
            case string or bool or long or double:
                return v;
            case sbyte or short or int or byte or ushort or uint:
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            case ulong u:
                return u > long.MaxValue ? u.ToString(CultureInfo.InvariantCulture) : (long)u;
            case float f:
                return (double)f;
            case TimeSpan t:
                return t.TotalMilliseconds;
            case DateTimeOffset at:
                return at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture);
            case Exception e:
                return e.Message;
            case IDictionary<string, object?> map:
                return Map(map.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)));
            case IReadOnlyDictionary<string, object?> map:
                return Map(map);
            case IDictionary map when StringKeyed(map):
                return Map(map.Cast<DictionaryEntry>().Select(p => new KeyValuePair<string, object?>((string)p.Key, p.Value)));
            case IEnumerable items:
                return items.Cast<object?>().Select(Normalize).ToList();
            default:
                return v.ToString() ?? "";
        }
    }

    private static SortedDictionary<string, object?> Map(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            sorted[key] = Normalize(value);
        }
        return sorted;
    }

    private static bool StringKeyed(IDictionary map)
    {
        foreach (var key in map.Keys)
        {
            if (key is not string)
            {
                return false;
            }
        }
        return true;
    }
}
