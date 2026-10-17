using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The combat lab's small JSON and text helpers.</summary>
internal static class SweepText
{
    /// <summary>An integer in invariant text.</summary>
    internal static string Tag(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>A number a proto3 default may have omitted: 0 when absent.</summary>
    internal static double NumberOrZero(this JsonNode? value) => value is null ? 0 : value.AsNumber();
}
