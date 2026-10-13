using System.Globalization;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>A parsed flag value, readable once <see cref="Flags.Parse"/> ran.</summary>
internal sealed class FlagValue<T>(T value)
{
    public T Value { get; internal set; } = value;
}

/// <summary>
/// The slice of Go's <c>flag</c> package <c>remoteaccept</c> uses: <c>-name value</c>, <c>-name=value</c>, <c>--name</c>,
/// boolean flags with an optional <c>=false</c>, and a parse that stops at the first argument that is not a flag.
/// </summary>
internal sealed class Flags
{
    private readonly Dictionary<string, Action<string?>> _setters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _booleans = new(StringComparer.Ordinal);

    /// <summary>The arguments left after the flags.</summary>
    public IReadOnlyList<string> Args { get; private set; } = [];

    public FlagValue<string> String(string name, string defaultValue)
    {
        var flag = new FlagValue<string>(defaultValue);
        _setters[name] = v => flag.Value = v ?? "";
        return flag;
    }

    public FlagValue<int> Int(string name, int defaultValue)
    {
        var flag = new FlagValue<int>(defaultValue);
        _setters[name] = v => flag.Value = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new RemoteAcceptUsageException($"invalid value \"{v}\" for flag -{name}: parse error");
        return flag;
    }

    public FlagValue<long> Int64(string name, long defaultValue)
    {
        var flag = new FlagValue<long>(defaultValue);
        _setters[name] = v => flag.Value = long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new RemoteAcceptUsageException($"invalid value \"{v}\" for flag -{name}: parse error");
        return flag;
    }

    public FlagValue<bool> Bool(string name, bool defaultValue)
    {
        var flag = new FlagValue<bool>(defaultValue);
        _booleans.Add(name);
        _setters[name] = v => flag.Value = v switch
        {
            null => true,
            "1" or "t" or "T" or "true" or "TRUE" or "True" => true,
            "0" or "f" or "F" or "false" or "FALSE" or "False" => false,
            _ => throw new RemoteAcceptUsageException($"invalid boolean value \"{v}\" for -{name}: parse error"),
        };
        return flag;
    }

    /// <summary>Parses <paramref name="args"/>; throws <see cref="RemoteAcceptUsageException"/> on an unknown flag or a bad value.</summary>
    public void Parse(IReadOnlyList<string> args)
    {
        var i = 0;
        for (; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Length < 2 || arg[0] != '-')
            {
                break;
            }

            if (arg == "--")
            {
                i++;
                break;
            }

            var name = arg.TrimStart('-');
            string? value = null;
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }

            if (!_setters.TryGetValue(name, out var setter))
            {
                throw new RemoteAcceptUsageException($"flag provided but not defined: -{name}");
            }

            if (value is null && !_booleans.Contains(name))
            {
                if (i + 1 >= args.Count)
                {
                    throw new RemoteAcceptUsageException($"flag needs an argument: -{name}");
                }

                value = args[++i];
            }

            setter(value);
        }

        Args = [.. args.Skip(i)];
    }
}
