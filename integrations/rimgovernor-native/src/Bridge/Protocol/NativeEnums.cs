#nullable enable
using System;
using RimWorld;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;
using Clock = RimGovernor.Protocol.Clock;

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

        internal static Obs.HungerCategory Hunger(HungerCategory hunger) => hunger switch
        {
            HungerCategory.Fed => Obs.HungerCategory.Fed,
            HungerCategory.Hungry => Obs.HungerCategory.Hungry,
            HungerCategory.UrgentlyHungry => Obs.HungerCategory.UrgentlyHungry,
            HungerCategory.Starving => Obs.HungerCategory.Starving,
            _ => Obs.HungerCategory.Unspecified
        };

        internal static Obs.RotStage Rot(RotStage stage) => stage switch
        {
            RotStage.Fresh => Obs.RotStage.Fresh,
            RotStage.Rotting => Obs.RotStage.Rotting,
            RotStage.Dessicated => Obs.RotStage.Dessicated,
            _ => Obs.RotStage.Unspecified
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

        // Waste is the waste read's kind ("corpse", "spoiled", "unwanted").
        internal static Obs.WasteKind Waste(string kind) => kind switch
        {
            "corpse" => Obs.WasteKind.Corpse,
            "spoiled" => Obs.WasteKind.Spoiled,
            "unwanted" => Obs.WasteKind.Unwanted,
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

        internal static Obs.Quality Quality(QualityCategory quality) => quality switch
        {
            QualityCategory.Awful => Obs.Quality.Awful,
            QualityCategory.Poor => Obs.Quality.Poor,
            QualityCategory.Normal => Obs.Quality.Normal,
            QualityCategory.Good => Obs.Quality.Good,
            QualityCategory.Excellent => Obs.Quality.Excellent,
            QualityCategory.Masterwork => Obs.Quality.Masterwork,
            QualityCategory.Legendary => Obs.Quality.Legendary,
            _ => Obs.Quality.Unspecified
        };

        internal static Obs.PriceType Price(RimWorld.PriceType price) => price switch
        {
            RimWorld.PriceType.Undefined => Obs.PriceType.Undefined,
            RimWorld.PriceType.VeryCheap => Obs.PriceType.VeryCheap,
            RimWorld.PriceType.Cheap => Obs.PriceType.Cheap,
            RimWorld.PriceType.Normal => Obs.PriceType.Normal,
            RimWorld.PriceType.Expensive => Obs.PriceType.Expensive,
            RimWorld.PriceType.Exorbitant => Obs.PriceType.Exorbitant,
            _ => Obs.PriceType.Unspecified
        };

        internal static Obs.TechLevel Tech(RimWorld.TechLevel level) => level switch
        {
            RimWorld.TechLevel.Undefined => Obs.TechLevel.Undefined,
            RimWorld.TechLevel.Animal => Obs.TechLevel.Animal,
            RimWorld.TechLevel.Neolithic => Obs.TechLevel.Neolithic,
            RimWorld.TechLevel.Medieval => Obs.TechLevel.Medieval,
            RimWorld.TechLevel.Industrial => Obs.TechLevel.Industrial,
            RimWorld.TechLevel.Spacer => Obs.TechLevel.Spacer,
            RimWorld.TechLevel.Ultra => Obs.TechLevel.Ultra,
            RimWorld.TechLevel.Archotech => Obs.TechLevel.Archotech,
            _ => Obs.TechLevel.Unspecified
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

        // The clock watcher records what held a force pause by name.
        internal static Clock.ForcePauseKind ForcePause(string? kind) => kind switch
        {
            "long_event" => Clock.ForcePauseKind.LongEvent,
            "transient_force_pause" => Clock.ForcePauseKind.Transient,
            _ => Clock.ForcePauseKind.Unspecified
        };
    }
}
