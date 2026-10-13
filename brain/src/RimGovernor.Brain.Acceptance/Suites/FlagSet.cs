using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A command-line mistake: an unknown flag, a missing or malformed value, a failed validation. The command prints the message and exits 2.</summary>
public sealed class UsageException : AcceptanceException
{
    public UsageException(string message)
        : base(message)
    {
    }

    public UsageException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>A parsed flag value, readable once <see cref="FlagSet.Parse"/> ran.</summary>
internal sealed class Flag<T>
{
    internal Flag(T value)
    {
        Value = value;
    }

    public T Value { get; internal set; }
}

/// <summary>
/// The small slice of Go's <c>flag</c> package the acceptance subcommands use: <c>-name value</c>, <c>-name=value</c>,
/// <c>--name</c>, boolean flags with an optional <c>=false</c>, and a parse that stops at the first argument that is not a
/// flag (case names precede the flags in several subcommands, so they split the list themselves first).
/// </summary>
internal sealed class FlagSet
{
    private readonly Dictionary<string, Action<string?>> _setters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _booleans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private readonly string _command;

    public FlagSet(string command)
    {
        _command = command;
    }

    /// <summary>The arguments left after the flags.</summary>
    public IReadOnlyList<string> Args { get; private set; } = [];

    /// <summary>Whether the flag appeared on the command line (Go <c>FlagSet.Visit</c>).</summary>
    public bool WasSet(string name) => _visited.Contains(name);

    public Flag<string> String(string name, string defaultValue)
    {
        var flag = new Flag<string>(defaultValue);
        _setters[name] = v => flag.Value = v ?? "";
        return flag;
    }

    public Flag<int> Int(string name, int defaultValue)
    {
        var flag = new Flag<int>(defaultValue);
        _setters[name] = v =>
        {
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new UsageException($"invalid value \"{v}\" for flag -{name}: parse error");
            }

            flag.Value = parsed;
        };
        return flag;
    }

    public Flag<bool> Bool(string name, bool defaultValue)
    {
        var flag = new Flag<bool>(defaultValue);
        _booleans.Add(name);
        _setters[name] = v =>
        {
            if (v is null)
            {
                flag.Value = true;
            }
            else if (bool.TryParse(v, out var parsed))
            {
                flag.Value = parsed;
            }
            else if (v is "1")
            {
                flag.Value = true;
            }
            else if (v is "0")
            {
                flag.Value = false;
            }
            else
            {
                throw new UsageException($"invalid boolean value \"{v}\" for -{name}: parse error");
            }
        };
        return flag;
    }

    public Flag<TimeSpan> Duration(string name, TimeSpan defaultValue)
    {
        var flag = new Flag<TimeSpan>(defaultValue);
        _setters[name] = v =>
        {
            if (v is null || !GoDuration.TryParse(v, out var parsed))
            {
                throw new UsageException($"invalid value \"{v}\" for flag -{name}: parse error");
            }

            flag.Value = parsed;
        };
        return flag;
    }

    /// <summary>Parses <paramref name="args"/>; throws <see cref="UsageException"/> on an unknown flag or a bad value.</summary>
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

            if (name is "h" or "help" && !_setters.ContainsKey(name))
            {
                throw new UsageException($"{_command}: help requested");
            }

            if (!_setters.TryGetValue(name, out var setter))
            {
                throw new UsageException($"flag provided but not defined: -{name}");
            }

            if (value is null && !_booleans.Contains(name))
            {
                if (i + 1 >= args.Count)
                {
                    throw new UsageException($"flag needs an argument: -{name}");
                }

                value = args[++i];
            }

            setter(value);
            _visited.Add(name);
        }

        Args = [.. args.Skip(i)];
    }

    /// <summary>Splits the leading non-flag arguments off (positional names that precede the flags) and parses the rest.</summary>
    public IReadOnlyList<string> ParseAfterPositionals(IReadOnlyList<string> args)
    {
        var count = 0;
        while (count < args.Count && !args[count].StartsWith('-'))
        {
            count++;
        }

        Parse([.. args.Skip(count)]);
        return [.. args.Take(count)];
    }
}
