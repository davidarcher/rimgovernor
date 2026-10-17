using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The combat lab's small text helpers.</summary>
internal static class SweepText
{
    /// <summary>An integer in invariant text.</summary>
    internal static string Tag(long n) => n.ToString(CultureInfo.InvariantCulture);
}
