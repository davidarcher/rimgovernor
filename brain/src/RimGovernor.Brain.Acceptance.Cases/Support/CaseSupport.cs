using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Small helpers shared by several case families. A family-specific helper lives beside its family
/// (<c>WallCases</c>, ...); only what two or more families need goes here, and each addition is a new member, never
/// an edit of an existing one, so concurrent ports merge cleanly.
/// </summary>
internal static class CaseSupport
{
    /// <summary>An integer as the invariant decimal text used in labels and signatures.</summary>
    internal static string Tag(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>A <c>{x, z}</c> cell object.</summary>
    internal static JsonObject Cell(int x, int z) => new() { ["x"] = x, ["z"] = z };

    /// <summary>
    /// The effect an applied action's receipt observed under <paramref name="effectKey"/>
    /// (<c>result.applied.applied.observed.&lt;effectKey&gt;</c>); null when the result is not an applied one.
    /// </summary>
    internal static JsonObject? AppliedEffect(JsonObject result, string effectKey)
    {
        var applied = result["applied"].AsMap()?["applied"].AsMap();
        return applied?["observed"].AsMap()?[effectKey].AsMap();
    }

    /// <summary>The prepared fixture reply's <c>center</c> cell (the lab op replies it).</summary>
    internal static (int X, int Z) Center(ISession s)
    {
        var center = s.Prepared?["center"].AsMap() ?? throw new AcceptanceException("the lab start replied no center");
        return ((int)center["x"].AsNumber(), (int)center["z"].AsNumber());
    }
}
