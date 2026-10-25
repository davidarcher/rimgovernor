using System;
using System.Text.RegularExpressions;

namespace RimGovernor.Host.Core.Tools;

/// <summary>The operation name shape the registry accepts: a lowercase name, optionally grouped by slashes.</summary>
public static class ToolNameValidator
{
    public const string CanonicalPattern = "^[a-z][a-z0-9_-]*(/[a-z][a-z0-9_-]*)*$";

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
            throw new ArgumentException($"Tool name '{name}' must match {CanonicalPattern}. Use lowercase operation names such as 'ping' or 'test/lab_start'; dotted names are not valid here.", valueName);
    }
}
