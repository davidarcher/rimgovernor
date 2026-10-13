using System;
using System.Text.RegularExpressions;

namespace RimGovernor.Host.Core.Tools;

/// <summary>The tool name shape the registry accepts: slash-delimited lowercase segments such as <c>rimgovernor/ping</c>.</summary>
public static class ToolNameValidator
{
    public const string CanonicalPattern = "^[a-z][a-z0-9_-]*(/[a-z][a-z0-9_-]*)+$";

    private static readonly Regex CanonicalToolNameRegex = new(CanonicalPattern, RegexOptions.CultureInvariant);

    public static bool IsCanonical(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && CanonicalToolNameRegex.IsMatch(name);
    }

    public static void EnsureCanonical(string name, string valueName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name cannot be null or empty.", valueName);

        if (!IsCanonical(name))
            throw new ArgumentException($"Tool name '{name}' must match {CanonicalPattern}. Use slash-delimited canonical names such as 'rimgovernor/ping'; dotted names are not valid here.", valueName);
    }
}
