using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using L = RimGovernor.Contracts.Wire.Lifecycle;
namespace RimGovernor.Brain.Bridge.Contracts;

public static class ColonyContexts
{
    public static bool SameColonyContext(ObservationIdentity actual, ObservationIdentity expected) =>
        actual.NativeGeneration is C.ObservedGeneration.AtCase a
        && expected.NativeGeneration is C.ObservedGeneration.AtCase b
        && actual.SameContext(expected)
        && a == b;

}
