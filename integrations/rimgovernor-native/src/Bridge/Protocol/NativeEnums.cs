using System;
using RimWorld;

namespace RimGovernor.Native
{
    // Vanilla enums onto their proto counterparts, one mapping each so
    // reads and writes share the same vocabulary.
    internal static partial class NativeEnums
    {
        internal static RimGovernor.Brain.GameFacts.Observations.Passion Passion(RimWorld.Passion passion) => passion switch
        {
            RimWorld.Passion.None => RimGovernor.Brain.GameFacts.Observations.Passion.None,
            RimWorld.Passion.Minor => RimGovernor.Brain.GameFacts.Observations.Passion.Minor,
            RimWorld.Passion.Major => RimGovernor.Brain.GameFacts.Observations.Passion.Major,
            _ => throw new ArgumentOutOfRangeException(nameof(passion), passion, "no wire passion")
        };

        internal static RimGovernor.Brain.GameFacts.Operations.MedicalCare Care(MedicalCareCategory care) => care switch
        {
            MedicalCareCategory.NoCare => RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoCare,
            MedicalCareCategory.NoMeds => RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoMedicine,
            MedicalCareCategory.HerbalOrWorse => RimGovernor.Brain.GameFacts.Operations.MedicalCare.HerbalOrWorse,
            MedicalCareCategory.NormalOrWorse => RimGovernor.Brain.GameFacts.Operations.MedicalCare.NormalOrWorse,
            MedicalCareCategory.Best => RimGovernor.Brain.GameFacts.Operations.MedicalCare.Best,
            _ => throw new ArgumentOutOfRangeException(nameof(care), care, "no wire medical care")
        };

        internal static MedicalCareCategory? Care(RimGovernor.Brain.GameFacts.Operations.MedicalCare care) => care switch
        {
            RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoCare => MedicalCareCategory.NoCare,
            RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoMedicine => MedicalCareCategory.NoMeds,
            RimGovernor.Brain.GameFacts.Operations.MedicalCare.HerbalOrWorse => MedicalCareCategory.HerbalOrWorse,
            RimGovernor.Brain.GameFacts.Operations.MedicalCare.NormalOrWorse => MedicalCareCategory.NormalOrWorse,
            RimGovernor.Brain.GameFacts.Operations.MedicalCare.Best => MedicalCareCategory.Best,
            _ => null
        };

        // The Anomaly enums fail loudly on a value the wire does not
        // name: the exception fails the native observation.
        internal static RimGovernor.Brain.GameFacts.Observations.EntityContainmentModeKind ContainmentMode(EntityContainmentMode mode) => mode switch
        {
            EntityContainmentMode.MaintainOnly => RimGovernor.Brain.GameFacts.Observations.EntityContainmentModeKind.MaintainOnly,
            EntityContainmentMode.Study => RimGovernor.Brain.GameFacts.Observations.EntityContainmentModeKind.Study,
            EntityContainmentMode.Release => RimGovernor.Brain.GameFacts.Observations.EntityContainmentModeKind.Release,
            EntityContainmentMode.Execute => RimGovernor.Brain.GameFacts.Observations.EntityContainmentModeKind.Execute,
            _ => throw new InvalidOperationException("EntityContainmentMode " + mode + " has no wire value.")
        };

        // Holder is the read's holder kind ("container", "corpse",
        // "pawnInventory", "carried") on the wire.
        internal static RimGovernor.Brain.GameFacts.Observations.HolderKind Holder(string kind) => kind switch
        {
            "container" => RimGovernor.Brain.GameFacts.Observations.HolderKind.Container,
            "corpse" => RimGovernor.Brain.GameFacts.Observations.HolderKind.Corpse,
            "pawnInventory" => RimGovernor.Brain.GameFacts.Observations.HolderKind.PawnInventory,
            "carried" => RimGovernor.Brain.GameFacts.Observations.HolderKind.Carried,
            _ => throw new InvalidOperationException("Unknown holder kind.")
        };

        internal static RimGovernor.Brain.GameFacts.Operations.RepeatMode Repeat(BillRepeatModeDef mode) =>
            mode == BillRepeatModeDefOf.Forever ? RimGovernor.Brain.GameFacts.Operations.RepeatMode.Forever
            : mode == BillRepeatModeDefOf.RepeatCount ? RimGovernor.Brain.GameFacts.Operations.RepeatMode.Count
            : mode == BillRepeatModeDefOf.TargetCount ? RimGovernor.Brain.GameFacts.Operations.RepeatMode.Target
            : throw new InvalidOperationException("Unknown bill repeat mode " + mode?.defName + ".");

        internal static RimGovernor.Brain.GameFacts.Operations.HostilityResponse Hostility(HostilityResponseMode mode) => mode switch
        {
            HostilityResponseMode.Ignore => RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Ignore,
            HostilityResponseMode.Attack => RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Attack,
            HostilityResponseMode.Flee => RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Flee,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "no wire hostility response")
        };

        internal static HostilityResponseMode? Hostility(RimGovernor.Brain.GameFacts.Operations.HostilityResponse response) => response switch
        {
            RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Ignore => HostilityResponseMode.Ignore,
            RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Attack => HostilityResponseMode.Attack,
            RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Flee => HostilityResponseMode.Flee,
            _ => null
        };

        internal static RimGovernor.Brain.GameFacts.Receipts.QuestStatus Quest(RimWorld.QuestState state) => state switch
        {
            RimWorld.QuestState.NotYetAccepted => RimGovernor.Brain.GameFacts.Receipts.QuestStatus.NotYetAccepted,
            RimWorld.QuestState.Ongoing => RimGovernor.Brain.GameFacts.Receipts.QuestStatus.Ongoing,
            RimWorld.QuestState.EndedUnknownOutcome => RimGovernor.Brain.GameFacts.Receipts.QuestStatus.EndedUnknownOutcome,
            RimWorld.QuestState.EndedSuccess => RimGovernor.Brain.GameFacts.Receipts.QuestStatus.EndedSuccess,
            RimWorld.QuestState.EndedFailed => RimGovernor.Brain.GameFacts.Receipts.QuestStatus.EndedFailed,
            RimWorld.QuestState.EndedOfferExpired => RimGovernor.Brain.GameFacts.Receipts.QuestStatus.EndedOfferExpired,
            RimWorld.QuestState.EndedInvalid => RimGovernor.Brain.GameFacts.Receipts.QuestStatus.EndedInvalid,
            _ => throw new System.InvalidOperationException("Unknown quest state " + state + ".")
        };
    }
}
