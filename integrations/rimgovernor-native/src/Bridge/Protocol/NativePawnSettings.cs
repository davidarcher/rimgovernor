#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // PawnSettingsIntent on Actions/Apply (#1299, epic #1292): one per-pawn
    // Assign-tab setting on one spawned colony pawn. hostility_response is
    // the pawn's playerSettings.hostilityResponse, where the Assign tab
    // offers it (UsesConfigurableHostilityResponse); Attack is refused for a
    // violence-incapable pawn, as the tab refuses it. self_tend (#1305) is
    // playerSettings.selfTend, refused for a pawn that cannot doctor (the
    // tab hides the checkbox). nickname (#1310) is the short name an owned
    // pawn must leave: a fresh name from the pawn's own name bank
    // (PawnBioAndNameGenerator), never a numbered one, that no other owned
    // pawn holds. medicine_carry (#1307) is the Medicine inventory-stock
    // count (ResolveCarry). medical_care (#1301) is playerSettings.medCare,
    // any of the five tiers, on a living pawn of the colony or hosted by it
    // (colonist, slave, prisoner, guest, tame animal). The other arms are
    // refused until their epic issues land.
    // A setting that already holds applies again.
    internal static class NativePawnSettings
    {
        // Every living named pawn the colony owns, on any map, caravan or
        // transporter: the player faction's pawns and the colony's prisoners.
        internal static IEnumerable<Pawn> OwnedNamedPawns()
        {
            var player = Faction.OfPlayerSilentFail;
            return PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive
                .Where(p => p.Name != null && (p.Faction == player && player != null || p.IsPrisonerOfColony))
                .OrderBy(p => p.thingIDNumber);
        }

        private static MedicalCareCategory? Care(Operations.MedicalCare care) => NativeEnums.Care(care);

        private static Common.Failure? Resolve(Operations.PawnSettingsIntent? intent, Common.ObservationContext context,
            out Pawn? pawn, out HostilityResponseMode mode, out MedicalCareCategory care)
        {
            pawn = null; mode = HostilityResponseMode.Attack; care = MedicalCareCategory.NoCare;
            if (intent == null || !ProtoBoundary.IsIdentifier(intent.PawnId))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn settings require an exact pawn.");
            var kind = intent.SettingCase;
            var id = intent.PawnId;
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.Nickname)
            {
                if (string.IsNullOrWhiteSpace(intent.Nickname))
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Nickname names the short name the pawn must leave.");
                pawn = OwnedNamedPawns().ById(id);
                return pawn == null ? ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living named pawn the colony owns is not found.") : null;
            }
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.MedicalCare)
            {
                var tier = Care(intent.MedicalCare);
                if (tier == null)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Medical care must be one of the five MedicalCareCategory tiers.");
                care = tier.Value;
                var player = Faction.OfPlayerSilentFail;
                pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(id);
                return pawn == null || pawn.Dead || pawn.playerSettings == null || player == null || pawn.Faction != player && pawn.HostFaction != player
                    ? ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living pawn of the colony with medical care settings is not spawned on this map.") : null;
            }
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.MedicineCarry)
                return ResolveCarry(intent, context, out pawn, out _);
            if (kind != Operations.PawnSettingsIntent.SettingOneofCase.HostilityResponse && kind != Operations.PawnSettingsIntent.SettingOneofCase.SelfTend)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Only the hostility_response, self_tend, nickname, medicine_carry and medical_care settings are supported.");
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.HostilityResponse)
            {
                if (NativeEnums.Hostility(intent.HostilityResponse) is not HostilityResponseMode parsed)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Hostility response must be Ignore, Attack or Flee.");
                mode = parsed;
            }
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(id);
            if (pawn == null || pawn.Dead || pawn.playerSettings == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living colony pawn with player settings is not spawned on this map.");
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.SelfTend)
                return pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                    ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A pawn that cannot doctor has no self-tend setting.") : null;
            if (!pawn.playerSettings.UsesConfigurableHostilityResponse)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living colony pawn with a configurable hostility response is not spawned on this map.");
            if (mode == HostilityResponseMode.Attack && pawn.WorkTagIsDisabled(WorkTags.Violent))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A violence-incapable pawn cannot be set to Attack.");
            return null;
        }

        internal static Common.Failure? Validate(Operations.PawnSettingsIntent? intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.PawnSettingsIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var mode, out var care);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            Receipts.FieldResult field;
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.Nickname) {
                field = new Receipts.FieldResult { Field = Receipts.SettingsField.Nickname, Outcome = Rename(pawn!, intent.Nickname) };
                return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                    Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn!.GetUniqueLoadID() },
                    Fields = { field } } };
            }
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.MedicineCarry)
                return ApplyCarry(intent, context);
            var settings = pawn!.playerSettings;
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.MedicalCare) {
                var outcome = settings.medCare == care ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied;
                settings.medCare = care;
                if (settings.medCare != care) throw new InvalidOperationException("Native medical care requires readback.");
                field = new Receipts.FieldResult { Field = Receipts.SettingsField.MedicalCare, Outcome = outcome };
            } else if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.SelfTend) {
                var want = intent.SelfTend;
                var outcome = settings.selfTend == want ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied;
                settings.selfTend = want;
                if (settings.selfTend != want) throw new InvalidOperationException("Native self-tend requires readback.");
                field = new Receipts.FieldResult { Field = Receipts.SettingsField.SelfTend, Outcome = outcome };
            } else {
                var outcome = settings.hostilityResponse == mode ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied;
                settings.hostilityResponse = mode;
                if (settings.hostilityResponse != mode) throw new InvalidOperationException("Native hostility response requires readback.");
                field = new Receipts.FieldResult { Field = Receipts.SettingsField.Hostility, Outcome = outcome };
            }
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn.GetUniqueLoadID() },
                Fields = { field } } };
        }

        // medicine_carry (#1307): the colonist's Medicine inventory-stock
        // count, stocking the best medicine the pawn's own medical care
        // allows; a positive count is refused when that care allows none.
        private static Common.Failure? ResolveCarry(Operations.PawnSettingsIntent intent, Common.ObservationContext context, out Pawn? pawn, out ThingDef? medicine)
        {
            pawn = null; medicine = null;
            var group = InventoryStockGroupDefOf.Medicine;
            if (intent.MedicineCarry < group.min || intent.MedicineCarry > group.max)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, $"Medicine carry must be {group.min}-{group.max}.");
            var id = intent.PawnId;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(id);
            if (pawn == null || pawn.Dead || pawn.playerSettings == null || pawn.inventoryStock == null || !pawn.IsColonist)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living colonist with an inventory stock is not spawned on this map.");
            var care = pawn.playerSettings.medCare;
            medicine = group.thingDefs.Where(d => care.AllowsMedicine(d))
                .OrderByDescending(d => d.GetStatValueAbstract(StatDefOf.MedicalPotency)).FirstOrDefault();
            return medicine == null && intent.MedicineCarry > 0
                ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The pawn's medical care allows no medicine to carry.") : null;
        }

        private static Receipts.EffectEvidence ApplyCarry(Operations.PawnSettingsIntent intent, Common.ObservationContext context)
        {
            var failure = ResolveCarry(intent, context, out var pawn, out var medicine);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var stock = pawn!.inventoryStock;
            var group = InventoryStockGroupDefOf.Medicine;
            var unchanged = stock.GetDesiredCountForGroup(group) == intent.MedicineCarry
                && (medicine == null || stock.GetDesiredThingForGroup(group) == medicine);
            if (medicine != null) stock.SetThingForGroup(group, medicine);
            stock.SetCountForGroup(group, intent.MedicineCarry);
            if (stock.GetDesiredCountForGroup(group) != intent.MedicineCarry) throw new InvalidOperationException("Native medicine carry requires readback.");
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.MedicineCarry,
                    Outcome = unchanged ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied } } } };
        }

        // Rename draws from the pawn's own name bank until a short name no
        // other owned pawn holds and that carries no digit; a humanlike keeps
        // its first and last name and takes the drawn short name as its nick.
        private static Receipts.FieldOutcome Rename(Pawn pawn, string leave)
        {
            if (!string.Equals(pawn.Name.ToStringShort, leave, StringComparison.OrdinalIgnoreCase)) return Receipts.FieldOutcome.Unchanged;
            var taken = new HashSet<string>(OwnedNamedPawns().Where(p => p != pawn).Select(p => p.Name.ToStringShort), StringComparer.OrdinalIgnoreCase) { leave };
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var drawn = PawnBioAndNameGenerator.GeneratePawnName(pawn, NameStyle.Full)?.ToStringShort;
                if (string.IsNullOrWhiteSpace(drawn) || drawn!.Any(char.IsDigit) || taken.Contains(drawn!)) continue;
                pawn.Name = pawn.Name is NameTriple triple ? new NameTriple(triple.First, drawn, triple.Last) : new NameSingle(drawn);
                if (pawn.Name.ToStringShort != drawn) throw new InvalidOperationException("Native nickname requires readback.");
                return Receipts.FieldOutcome.Applied;
            }
            throw new ApplyRefusedException(Common.FailureCode.CapacityExhausted, "The pawn's name bank offered no unused, unnumbered name.");
        }
    }

    internal sealed class PawnSettingsActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativePawnSettings.Validate(action.PawnSettings, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativePawnSettings.Apply(action.PawnSettings, context);
    }
}
