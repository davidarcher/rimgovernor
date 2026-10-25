#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // BuildingPatchIntent's medical, for_prisoners and for_slaves arms. Any bed may be
    // flagged medical (an animal bed or sleeping spot included); prisoner and
    // slave use stay humanlike-only. Building_Bed.Medical's setter early-returns on an equal value, otherwise calls
    // RemoveAllOwners() and re-scores the room role. A bed whose def has
    // bed_canBeMedical false is refused rather than written, because the
    // game's setter would silently ignore it.
    internal static class NativeBedUse
    {


        // Prisoners reports a for_prisoners write: only true, and
        // through ForOwnerType, never ForPrisoners, whose false arm is a
        // Log.Error (see BuildingConfigTool.cs). It drops every owner.
        internal static bool Prisoners(Operations.BuildingPatchIntent intent) => intent.Change is Operations.BuildingPatchIntentChange.ForPrisonersCase;

        // Slaves reports a for_slaves write: ForOwnerType = Slave,
        // which the game's setter accepts only under Ideology. It drops
        // every owner.
        internal static bool Slaves(Operations.BuildingPatchIntent intent) => intent.Change is Operations.BuildingPatchIntentChange.ForSlavesCase;

        private static bool Matches(Building_Bed bed, Operations.BuildingPatchIntent intent) =>
            Prisoners(intent) ? bed.ForPrisoners : Slaves(intent) ? bed.ForSlaves : intent.Change is Operations.BuildingPatchIntentChange.MedicalCase { Value: var medical } && bed.Medical == medical;

        internal static bool Eligible(Thing thing) => thing is Building_Bed bed && !bed.Destroyed && bed.Spawned
            && ProtoBoundary.IsLoaded(bed.Map) && bed.def?.building != null;

        // Control carries the bed's writable medical state on the building
        // listing row; the settable flag itself is only implied (a refused
        // preview names the gate).
        internal static Obs.BuildingControl.Bed Control(Building_Bed bed) =>
            new(bed.Medical, bed.ForPrisoners,
                [.. bed.OwnersForReading.Select(owner => NativeRef.Of(owner)!)]);

        private static Common.Failure? Resolve(Operations.BuildingPatchIntent intent, Common.ObservationContext context, out Building_Bed? bed)
        {
            bed = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A bed patch requires an exact bed.");
            var thing = RefIndex.Thing(ProtoBoundary.LoadedMap(context), intent.ThingId);
            if (thing == null || !Eligible(thing)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact bed is unavailable.");
            bed = (Building_Bed)thing;
            if ((Prisoners(intent) || Slaves(intent)) && !bed.def.building.bed_humanlike)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Only a humanlike bed can be for prisoners or slaves.");
            if (Prisoners(intent) && (bed.ForHumanBabies || !(bed.GetRoom() is Room room) || !Building_Bed.RoomCanBePrisonCell(room)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed cannot hold a prisoner: a crib, or its room cannot be a prison cell.");
            if (Slaves(intent) && (!ModsConfig.IdeologyActive || bed.ForHumanBabies || bed.Medical))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed cannot be for slaves: Ideology inactive, a crib, or a medical bed.");
            if (!Prisoners(intent) && !Slaves(intent) && intent.Change is Operations.BuildingPatchIntentChange.MedicalCase { Value: true } && !bed.def.building.bed_canBeMedical)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed definition cannot be medical; the game's setter would ignore the write.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Operations.BuildingPatchIntent intent) =>
            new Receipts.EffectEvidence { Effect = new Receipts.SettingsEffect {
                Fields = [new Receipts.FieldResult { Field = Prisoners(intent) ? Receipts.SettingsField.PrisonerBed : Slaves(intent) ? Receipts.SettingsField.SlaveBed : Receipts.SettingsField.MedicalBed,
                    Outcome = Receipts.FieldOutcome.Applied }] } };

        internal static Common.Failure? Validate(Operations.BuildingPatchIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static Receipts.EffectEvidence Apply(Operations.BuildingPatchIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var bed);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Prisoners(intent)) { if (!bed!.ForPrisoners) bed.ForOwnerType = BedOwnerType.Prisoner; }
            else if (Slaves(intent)) { if (!bed!.ForSlaves) bed.ForOwnerType = BedOwnerType.Slave; }
            else if (intent.Change is Operations.BuildingPatchIntentChange.MedicalCase { Value: var medical }) bed!.Medical = medical;
            else throw new InvalidOperationException("Bed use setting missing");
            if (!Matches(bed, intent)) throw new InvalidOperationException("Native bed use requires readback.");
            return Evidence(intent);
        }
    }
}
