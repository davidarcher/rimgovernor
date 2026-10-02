#nullable enable
using System;
using RimWorld;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    // Vanilla enums onto their proto counterparts, one mapping each so
    // reads and writes share the same vocabulary (#1341).
    internal static class NativeEnums
    {
        internal static Obs.Passion Passion(RimWorld.Passion passion) => passion switch
        {
            RimWorld.Passion.None => Obs.Passion.None,
            RimWorld.Passion.Minor => Obs.Passion.Minor,
            RimWorld.Passion.Major => Obs.Passion.Major,
            _ => Obs.Passion.Unspecified
        };

        internal static Operations.MedicalCare Care(MedicalCareCategory care) => care switch
        {
            MedicalCareCategory.NoCare => Operations.MedicalCare.NoCare,
            MedicalCareCategory.NoMeds => Operations.MedicalCare.NoMedicine,
            MedicalCareCategory.HerbalOrWorse => Operations.MedicalCare.HerbalOrWorse,
            MedicalCareCategory.NormalOrWorse => Operations.MedicalCare.NormalOrWorse,
            MedicalCareCategory.Best => Operations.MedicalCare.Best,
            _ => Operations.MedicalCare.Unspecified
        };

        internal static MedicalCareCategory? Care(Operations.MedicalCare care) => care switch
        {
            Operations.MedicalCare.NoCare => MedicalCareCategory.NoCare,
            Operations.MedicalCare.NoMedicine => MedicalCareCategory.NoMeds,
            Operations.MedicalCare.HerbalOrWorse => MedicalCareCategory.HerbalOrWorse,
            Operations.MedicalCare.NormalOrWorse => MedicalCareCategory.NormalOrWorse,
            Operations.MedicalCare.Best => MedicalCareCategory.Best,
            _ => null
        };

        internal static Operations.HostilityResponse Hostility(HostilityResponseMode mode) => mode switch
        {
            HostilityResponseMode.Ignore => Operations.HostilityResponse.Ignore,
            HostilityResponseMode.Attack => Operations.HostilityResponse.Attack,
            HostilityResponseMode.Flee => Operations.HostilityResponse.Flee,
            _ => Operations.HostilityResponse.Unspecified
        };

        internal static HostilityResponseMode? Hostility(Operations.HostilityResponse response) => response switch
        {
            Operations.HostilityResponse.Ignore => HostilityResponseMode.Ignore,
            Operations.HostilityResponse.Attack => HostilityResponseMode.Attack,
            Operations.HostilityResponse.Flee => HostilityResponseMode.Flee,
            _ => null
        };
    }
}
