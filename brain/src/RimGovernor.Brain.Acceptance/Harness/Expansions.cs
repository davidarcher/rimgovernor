using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Official-expansion package naming (Go <c>headless.go</c>): the base game is exactly <see cref="CorePackage"/>.</summary>
public static class Expansions
{
    public const string CorePackage = "ludeon.rimworld";
    public const string ExpansionPrefix = "ludeon.rimworld.";

    /// <summary>The environment variable a run may set to opt specific expansions in when a Config leaves Expansions null.</summary>
    public const string ExpansionsEnv = "RIMGOVERNOR_ACCEPT_EXPANSIONS";

    /// <summary>Normalizes a short expansion name ("royalty") or a full package id to its casefolded package id.</summary>
    public static string ExpansionPackage(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string folded = name.Trim().ToLowerInvariant();
        if (folded.Length == 0 || folded == CorePackage)
        {
            throw new AcceptanceException($"expansion name must not be empty or the core package: \"{name}\"");
        }

        if (!folded.StartsWith(ExpansionPrefix, StringComparison.Ordinal))
        {
            folded = ExpansionPrefix + folded;
        }

        if (folded[ExpansionPrefix.Length..].IndexOfAny(['.', ' ', '\t']) >= 0)
        {
            throw new AcceptanceException($"invalid expansion name: \"{name}\"");
        }

        return folded;
    }

    /// <summary>Parses <see cref="ExpansionsEnv"/>; unset or empty returns null, which the profile preparers read as every installed expansion.</summary>
    public static IReadOnlyList<string>? FromEnvironment()
    {
        string raw = (Environment.GetEnvironmentVariable(ExpansionsEnv) ?? "").Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var result = new List<string>();
        foreach (string part in raw.Split(','))
        {
            if (part.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                result.Add(ExpansionPackage(part));
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{ExpansionsEnv}: {e.Message}"), e);
            }
        }

        return result;
    }
}
