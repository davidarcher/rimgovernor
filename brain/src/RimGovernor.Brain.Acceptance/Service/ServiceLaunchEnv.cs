namespace RimGovernor.Brain.Acceptance;

/// <summary>Reads <see cref="ServeSpec.Env"/> entries (KEY=value) for the game's launch environment.</summary>
public static class ServiceLaunchEnv
{
    /// <summary>The name and value of one <c>KEY=value</c> entry (no value when there is no "=").</summary>
    public static (string Name, string Value) Split(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var at = entry.IndexOf('=', StringComparison.Ordinal);
        return at < 0 ? (entry, "") : (entry[..at], entry[(at + 1)..]);
    }

    /// <summary>Whether <paramref name="name"/> reads like an environment variable (upper-case letters, digits and underscores).</summary>
    public static bool IsVariable(string name) =>
        name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>The entries that can be applied to a launch environment, as (name, value) pairs.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Applied(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            var (name, value) = Split(entry);
            if (IsVariable(name))
            {
                yield return KeyValuePair.Create(name, value);
            }
        }
    }
}
