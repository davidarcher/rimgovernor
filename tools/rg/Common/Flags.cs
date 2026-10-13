using System.Globalization;

namespace RimGovernor.Tools.Common;

/// <summary>
/// Go-flag-style parsing: <c>-name</c> or <c>--name</c>, <c>-name value</c> or <c>-name=value</c>; flags stop at the
/// first non-flag argument or <c>--</c>. Booleans are bare (or <c>-name=false</c>).
/// </summary>
internal sealed class Flags
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Positional { get; private set; } = [];

    public bool HelpRequested { get; private set; }

    public static Flags Parse(string[] args, IReadOnlySet<string> boolFlags, IReadOnlySet<string> valueFlags)
    {
        var flags = new Flags();
        var i = 0;
        for (; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                i++;
                break;
            }
            if (arg.Length < 2 || arg[0] != '-')
            {
                break;
            }
            var name = arg.TrimStart('-');
            string? inline = null;
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                inline = name[(eq + 1)..];
                name = name[..eq];
            }
            if (name is "h" or "help")
            {
                flags.HelpRequested = true;
                continue;
            }
            if (boolFlags.Contains(name))
            {
                flags._values[name] = inline ?? "true";
            }
            else if (valueFlags.Contains(name))
            {
                if (inline is null)
                {
                    i++;
                    if (i >= args.Length)
                    {
                        throw new UsageException($"flag needs an argument: -{name}");
                    }
                    inline = args[i];
                }
                flags._values[name] = inline;
            }
            else
            {
                throw new UsageException($"flag provided but not defined: -{name}");
            }
        }
        flags.Positional = args[i..];
        return flags;
    }

    public string String(string name, string fallback = "") => _values.TryGetValue(name, out var v) ? v : fallback;

    public bool Bool(string name) => _values.TryGetValue(name, out var v) && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    public int Int(string name, int fallback = 0)
    {
        if (!_values.TryGetValue(name, out var v))
        {
            return fallback;
        }
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new UsageException($"invalid value \"{v}\" for flag -{name}");
    }

    /// <summary>Go duration text such as <c>15m</c>, <c>90s</c>, <c>1h30m</c>.</summary>
    public TimeSpan Duration(string name, TimeSpan fallback)
    {
        if (!_values.TryGetValue(name, out var v))
        {
            return fallback;
        }
        return Durations.TryParse(v, out var d) ? d : throw new UsageException($"invalid value \"{v}\" for flag -{name}");
    }
}
