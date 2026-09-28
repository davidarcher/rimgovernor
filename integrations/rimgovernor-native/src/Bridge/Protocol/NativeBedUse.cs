#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // BuildingPatchIntent's medical, for_prisoners and for_slaves arms. Building_Bed
    // .Medical's setter early-returns on an equal value, otherwise calls
    // RemoveAllOwners() and re-scores the room role. A bed whose def has
    // bed_canBeMedical false is refused rather than written, because the
    // game's setter would silently ignore it.
    internal static class NativeBedUse
    {


        // Prisoners reports a for_prisoners write (#880): only true, and
        // through ForOwnerType, never ForPrisoners, whose false arm is a
        // Log.Error (see BuildingConfigTool.cs). It drops every owner.
        internal static bool Prisoners(Operations.BuildingPatchIntent intent) => intent.ChangeCase == Operations.BuildingPatchIntent.ChangeOneofCase.ForPrisoners;

        // Slaves reports a for_slaves write (#1036): ForOwnerType = Slave,
        // which the game's setter accepts only under Ideology. It drops
        // every owner.
        internal static bool Slaves(Operations.BuildingPatchIntent intent) => intent.ChangeCase == Operations.BuildingPatchIntent.ChangeOneofCase.ForSlaves;

        private static bool Matches(Building_Bed bed, Operations.BuildingPatchIntent intent) =>
            Prisoners(intent) ? bed.ForPrisoners : Slaves(intent) ? bed.ForSlaves : bed.Medical == intent.Medical;

        internal static bool Eligible(Thing thing) => thing is Building_Bed bed && !bed.Destroyed && bed.Spawned
            && ProtoBoundary.IsLoaded(bed.Map) && bed.def?.building != null && bed.def.building.bed_humanlike;

        internal static string Token(Common.Identity identity, Building_Bed bed)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(bed.GetUniqueLoadID()); writer.Write(bed.Medical); writer.Write(bed.ForPrisoners); writer.Write(bed.ForSlaves);
                    foreach (var owner in bed.OwnersForReading.Select(p => p.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal))
                        writer.Write(owner);
                }
                using (var hash = SHA256.Create())
                    return "bed-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, (Building_Bed)thing) };
        }

        // Settings carries the bed's writable medical state beside its CAS
        // snapshot on the building listing row; the settable flag itself is
        // only implied (a refused preview names the gate).
        internal static Obs.BuildingSettings Settings(Building_Bed bed, Common.ObservationContext context)
        {
            var settings = new Obs.BuildingSettings { Snapshot = Snapshot(bed, context), Medical = bed.Medical, ForPrisoners = bed.ForPrisoners };
            foreach (var owner in bed.OwnersForReading) settings.AssignedPawnIds.Add(owner.GetUniqueLoadID());
            return settings;
        }

        private static Common.Failure? Resolve(Operations.BuildingPatchIntent intent, Common.ObservationContext context, out Building_Bed? bed)
        {
            bed = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A bed patch requires an exact bed.");
            var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == intent.ThingId);
            if (thing == null || !Eligible(thing)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact humanlike bed is unavailable.");
            bed = (Building_Bed)thing;
            if (Prisoners(intent) && (bed.ForHumanBabies || !(bed.GetRoom() is Room room) || !Building_Bed.RoomCanBePrisonCell(room)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed cannot hold a prisoner: a crib, or its room cannot be a prison cell.");
            if (Slaves(intent) && (!ModsConfig.IdeologyActive || bed.ForHumanBabies || bed.Medical))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed cannot be for slaves: Ideology inactive, a crib, or a medical bed.");
            if (!Prisoners(intent) && !Slaves(intent) && intent.Medical && !bed.def.building.bed_canBeMedical)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed definition cannot be medical; the game's setter would ignore the write.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Operations.BuildingPatchIntent intent, string after) =>
            new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = intent.ThingId, AfterToken = after },
                Fields = { new Receipts.FieldResult { Field = Prisoners(intent) ? Receipts.SettingsField.PrisonerBed : Slaves(intent) ? Receipts.SettingsField.SlaveBed : Receipts.SettingsField.MedicalBed,
                    Outcome = Receipts.FieldOutcome.Applied } } } };

        internal static Common.Failure? Validate(Operations.BuildingPatchIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static Receipts.EffectEvidence Apply(Operations.BuildingPatchIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var bed);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Prisoners(intent)) { if (!bed!.ForPrisoners) bed.ForOwnerType = BedOwnerType.Prisoner; }
            else if (Slaves(intent)) { if (!bed!.ForSlaves) bed.ForOwnerType = BedOwnerType.Slave; }
            else bed!.Medical = intent.Medical;
            if (!Matches(bed, intent)) throw new InvalidOperationException("Native bed use requires readback.");
            return Evidence(intent, Snapshot(bed, context)!.Token);
        }
    }
}
