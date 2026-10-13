using RimGovernor.Brain.Policy;
using WireFactFamily = RimGovernor.Protocol.Clock.FactFamily;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go families.go: the bridge's FactFamily IS the policy registry's FactFamily (a Go type alias), so the C#
// type is RimGovernor.Brain.Policy.FactFamily and only the wire mapping lives here.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>FactFamilies()</c>: every family, in wire order.</summary>
    public static IReadOnlyList<FactFamily> FactFamilies() => PolicyFunctions.FactFamilies();

    /// <summary>
    /// Go's <c>FactFamilyFromWire</c>: maps a clock ObservationInvalidated family to its controller name; an
    /// unspecified or unknown value reports false.
    /// </summary>
    public static bool FactFamilyFromWire(WireFactFamily value, out FactFamily family)
    {
        switch (value)
        {
            case WireFactFamily.Definitions:
                family = FactFamily.Definitions;
                return true;
            case WireFactFamily.World:
                family = FactFamily.World;
                return true;
            case WireFactFamily.Identity:
                family = FactFamily.Identity;
                return true;
            case WireFactFamily.Colony:
                family = FactFamily.Colony;
                return true;
            case WireFactFamily.Pawns:
                family = FactFamily.Pawns;
                return true;
            case WireFactFamily.Emergency:
                family = FactFamily.Emergency;
                return true;
            case WireFactFamily.Rooms:
                family = FactFamily.Rooms;
                return true;
            case WireFactFamily.Research:
                family = FactFamily.Research;
                return true;
            default:
                family = default;
                return false;
        }
    }
}
