#nullable enable
using System;
using RimWorld;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Vanilla enums onto their proto counterparts, one mapping each so
    // reads and writes share the same vocabulary.
    internal static partial class NativeEnums
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

        // The Anomaly enums fail loudly on a value the wire does not
        // name: the caller turns the exception into a ReadIssue.
        internal static Obs.EntityContainmentModeKind ContainmentMode(EntityContainmentMode mode) => mode switch
        {
            EntityContainmentMode.MaintainOnly => Obs.EntityContainmentModeKind.MaintainOnly,
            EntityContainmentMode.Study => Obs.EntityContainmentModeKind.Study,
            EntityContainmentMode.Release => Obs.EntityContainmentModeKind.Release,
            EntityContainmentMode.Execute => Obs.EntityContainmentModeKind.Execute,
            _ => throw new InvalidOperationException("EntityContainmentMode " + mode + " has no wire value.")
        };

        internal static Common.RotStage Rot(RotStage stage) => stage switch
        {
            RotStage.Fresh => Common.RotStage.Fresh,
            RotStage.Rotting => Common.RotStage.Rotting,
            RotStage.Dessicated => Common.RotStage.Dessicated,
            _ => Common.RotStage.Unspecified
        };

        // Holder is the read's holder kind ("container", "corpse",
        // "pawnInventory", "carried") on the wire.
        internal static Obs.HolderKind Holder(string kind) => kind switch
        {
            "container" => Obs.HolderKind.Container,
            "corpse" => Obs.HolderKind.Corpse,
            "pawnInventory" => Obs.HolderKind.PawnInventory,
            "carried" => Obs.HolderKind.Carried,
            _ => throw new InvalidOperationException("Unknown holder kind.")
        };

        // Waste is the waste read's kind ("corpse", "spoiled").
        internal static Obs.WasteKind Waste(string kind) => kind switch
        {
            "corpse" => Obs.WasteKind.Corpse,
            "spoiled" => Obs.WasteKind.Spoiled,
            _ => throw new InvalidOperationException("Unknown waste kind.")
        };

        internal static Operations.RepeatMode Repeat(BillRepeatModeDef mode) =>
            mode == BillRepeatModeDefOf.Forever ? Operations.RepeatMode.Forever
            : mode == BillRepeatModeDefOf.RepeatCount ? Operations.RepeatMode.Count
            : mode == BillRepeatModeDefOf.TargetCount ? Operations.RepeatMode.Target : Operations.RepeatMode.Unspecified;

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

        internal static Receipts.QuestStatus Quest(RimWorld.QuestState state) => state switch
        {
            RimWorld.QuestState.NotYetAccepted => Receipts.QuestStatus.NotYetAccepted,
            RimWorld.QuestState.Ongoing => Receipts.QuestStatus.Ongoing,
            RimWorld.QuestState.EndedUnknownOutcome => Receipts.QuestStatus.EndedUnknownOutcome,
            RimWorld.QuestState.EndedSuccess => Receipts.QuestStatus.EndedSuccess,
            RimWorld.QuestState.EndedFailed => Receipts.QuestStatus.EndedFailed,
            RimWorld.QuestState.EndedOfferExpired => Receipts.QuestStatus.EndedOfferExpired,
            RimWorld.QuestState.EndedInvalid => Receipts.QuestStatus.EndedInvalid,
            _ => Receipts.QuestStatus.Unspecified
        };
    }
}
