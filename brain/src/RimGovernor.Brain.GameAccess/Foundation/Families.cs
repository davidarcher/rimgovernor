using RimGovernor.Brain.Policy;
using WireFactFamily = RimGovernor.Brain.GameFacts.Clock.FactFamily;

namespace RimGovernor.Brain.GameAccess;

// The bridge's FactFamily IS the policy registry's FactFamily, so the C#
// type is RimGovernor.Brain.Policy.FactFamily and only the wire mapping lives here.
public static class Families
{
    /// <summary>Every family, in wire order.</summary>
    public static IReadOnlyList<FactFamily> FactFamilies() => FactFamilyRules.FactFamilies();

    /// <summary>
    /// Maps a clock ObservationInvalidated family to its controller name; an
    /// unspecified or unknown value reports false.
    /// </summary>
    private static readonly Dictionary<WireFactFamily, FactFamily> FromWire = new Dictionary<
        WireFactFamily,
        FactFamily
    >
    {
        [WireFactFamily.Definitions] = FactFamily.Definitions,
        [WireFactFamily.World] = FactFamily.World,
        [WireFactFamily.Identity] = FactFamily.Identity,
        [WireFactFamily.Colony] = FactFamily.Colony,
        [WireFactFamily.Pawns] = FactFamily.Pawns,
        [WireFactFamily.Emergency] = FactFamily.Emergency,
        [WireFactFamily.Rooms] = FactFamily.Rooms,
        [WireFactFamily.Research] = FactFamily.Research,
    };

    public static bool FactFamilyFromWire(WireFactFamily value, out FactFamily family) =>
        FromWire.TryGetValue(value, out family);
}
