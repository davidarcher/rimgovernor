using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
namespace RimGovernor.Brain.GameAccess;

public static class ColonyContexts
{
    public static bool SameColonyContext(ObservationIdentity actual, ObservationIdentity expected) =>
        actual.NativeGeneration is RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase a
        && expected.NativeGeneration is RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase b
        && actual.SameContext(expected)
        && a == b;

}
