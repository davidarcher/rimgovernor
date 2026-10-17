using System.Globalization;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Small helpers shared by several case families. A family-specific helper lives beside its family; only what two or
/// more families need goes here.
/// </summary>
internal static class CaseSupport
{
    /// <summary>An integer as the invariant decimal text used in labels and signatures.</summary>
    internal static string Tag(long n) => n.ToString(CultureInfo.InvariantCulture);
}
