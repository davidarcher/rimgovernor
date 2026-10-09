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
    // PawnSettingsIntent on Actions/Apply: one per-pawn
    // Assign-tab setting on one spawned colony pawn. hostility_response is
    // the pawn's playerSettings.hostilityResponse, where the Assign tab
    // offers it (UsesConfigurableHostilityResponse); Attack is refused for a
    // violence-incapable pawn, as the tab refuses it. self_tend is
    // playerSettings.selfTend, refused for a pawn that cannot doctor (the
    // tab hides the checkbox). nickname is the short name an owned
    // pawn must leave: a fresh name from the pawn's own name bank
    // (PawnBioAndNameGenerator), never a numbered one, that no other owned
    // pawn holds. medicine_carry is the Medicine inventory-stock
    // count (ResolveCarry). medical_care is playerSettings.medCare,
    // any of the five tiers, on a living pawn of the colony or hosted by it
    // (colonist, slave, prisoner, guest, tame animal). reading_policy
    // assigns the one ReadingPolicy carrying that label to a spawned
    // pawn of the colony with a reading tracker; drug_policy the one
    // DrugPolicy carrying that label to one with a drug tracker; food_policy
    // the one FoodPolicy carrying that label to one with a food
    // restriction tracker. mech_work_mode and mech_control_group take
    // a controllable mechanoid of the colony whose overseer is a living
    // colonist with a mechanitor tracker (never a wild, hostile or
    // unoverseen mech): the first sets the MechWorkModeDef of the mech's
    // control group, the second moves the mech into one of its overseer's
    // control groups; both read the group back. choose_permit spends
    // a colonist's permit points on one permit of a faction through
    // Pawn_RoyaltyTracker.AddPermit after the checks the game's permit window
    // (PermitsCardUtility) applies, and reads the held permit back.
    // extract_bioferrite is a held entity's
    // CompHoldingPlatformTarget.extractBioferrite, the flag the game's own
    // Doctor work giver reads; it takes an entity a holding platform holds on
    // the map and reads the flag back.
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
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.ReadingPolicy)
                return ResolveReading(intent, context, out pawn, out _);
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.DrugPolicy)
                return ResolveDrug(intent, context, out pawn, out _);
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.FoodPolicy)
                return ResolveFood(intent, context, out pawn, out _);
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.MechWorkMode || kind == Operations.PawnSettingsIntent.SettingOneofCase.MechControlGroup)
                return ResolveMech(intent, context, out pawn, out _, out _, out _);
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.ChoosePermit)
                return ResolvePermit(intent, out pawn, out _, out _);
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.ExtractBioferrite)
                return ResolveExtractBioferrite(intent, context, out pawn, out _);
            if (kind != Operations.PawnSettingsIntent.SettingOneofCase.HostilityResponse && kind != Operations.PawnSettingsIntent.SettingOneofCase.SelfTend)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn settings require exactly one setting.");
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
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.ReadingPolicy)
                return ApplyReading(intent, context);
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.DrugPolicy)
                return ApplyDrug(intent, context);
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.FoodPolicy)
                return ApplyFood(intent, context);
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.MechWorkMode || intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.MechControlGroup)
                return ApplyMech(intent, context);
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.ChoosePermit)
                return ApplyPermit(intent);
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.ExtractBioferrite)
                return ApplyExtractBioferrite(intent, context);
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

        // medicine_carry: the colonist's Medicine inventory-stock
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

        // reading_policy: the one ReadingPolicy labelled with the
        // intent's name, on a spawned pawn of the colony that reads.
        private static Common.Failure? ResolveReading(Operations.PawnSettingsIntent intent, Common.ObservationContext context, out Pawn? pawn, out ReadingPolicy? policy)
        {
            pawn = null; policy = null;
            var name = intent.ReadingPolicy;
            if (!ProtoBoundary.IsIdentifier(name))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Reading policy names the policy label.");
            var matches = Current.Game.readingPolicyDatabase.AllReadingPolicies.Where(p => p.label == name).ToList();
            if (matches.Count != 1)
                return ProtoBoundary.Fail(matches.Count == 0 ? Common.FailureCode.NotFound : Common.FailureCode.InvalidRequest, "Exactly one reading policy must carry this label.");
            policy = matches[0];
            var id = intent.PawnId;
            var player = Faction.OfPlayerSilentFail;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == id);
            return pawn == null || pawn.Dead || pawn.reading == null || player == null || pawn.Faction != player && pawn.HostFaction != player
                ? ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living pawn of the colony with a reading policy is not spawned on this map.") : null;
        }

        private static Receipts.EffectEvidence ApplyReading(Operations.PawnSettingsIntent intent, Common.ObservationContext context)
        {
            var failure = ResolveReading(intent, context, out var pawn, out var policy);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var unchanged = pawn!.reading.CurrentPolicy == policy;
            pawn.reading.CurrentPolicy = policy;
            if (pawn.reading.CurrentPolicy != policy) throw new InvalidOperationException("Native reading policy requires readback.");
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.ReadingPolicy,
                    Outcome = unchanged ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied } } } };
        }

        // drug_policy: the one DrugPolicy labelled with the intent's
        // name, on a spawned pawn of the colony with a drug tracker.
        private static Common.Failure? ResolveDrug(Operations.PawnSettingsIntent intent, Common.ObservationContext context, out Pawn? pawn, out DrugPolicy? policy)
        {
            pawn = null; policy = null;
            var name = intent.DrugPolicy;
            if (!ProtoBoundary.IsIdentifier(name))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Drug policy names the policy label.");
            var matches = Current.Game.drugPolicyDatabase.AllPolicies.Where(p => p.label == name).ToList();
            if (matches.Count != 1)
                return ProtoBoundary.Fail(matches.Count == 0 ? Common.FailureCode.NotFound : Common.FailureCode.InvalidRequest, "Exactly one drug policy must carry this label.");
            policy = matches[0];
            var id = intent.PawnId;
            var player = Faction.OfPlayerSilentFail;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == id);
            return pawn == null || pawn.Dead || pawn.drugs == null || player == null || pawn.Faction != player && pawn.HostFaction != player
                ? ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living pawn of the colony with a drug policy is not spawned on this map.") : null;
        }

        private static Receipts.EffectEvidence ApplyDrug(Operations.PawnSettingsIntent intent, Common.ObservationContext context)
        {
            var failure = ResolveDrug(intent, context, out var pawn, out var policy);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var unchanged = pawn!.drugs.CurrentPolicy == policy;
            pawn.drugs.CurrentPolicy = policy;
            if (pawn.drugs.CurrentPolicy != policy) throw new InvalidOperationException("Native drug policy requires readback.");
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.DrugPolicy,
                    Outcome = unchanged ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied } } } };
        }

        // food_policy: the one FoodPolicy labelled with the intent's
        // name, on a spawned pawn of the colony with a food restriction.
        private static Common.Failure? ResolveFood(Operations.PawnSettingsIntent intent, Common.ObservationContext context, out Pawn? pawn, out FoodPolicy? policy)
        {
            pawn = null; policy = null;
            var name = intent.FoodPolicy;
            if (!ProtoBoundary.IsIdentifier(name))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Food policy names the policy label.");
            var matches = Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Where(p => p.label == name).ToList();
            if (matches.Count != 1)
                return ProtoBoundary.Fail(matches.Count == 0 ? Common.FailureCode.NotFound : Common.FailureCode.InvalidRequest, "Exactly one food policy must carry this label.");
            policy = matches[0];
            var id = intent.PawnId;
            var player = Faction.OfPlayerSilentFail;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == id);
            return pawn == null || pawn.Dead || pawn.foodRestriction == null || player == null || pawn.Faction != player && pawn.HostFaction != player
                ? ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living pawn of the colony with a food policy is not spawned on this map.") : null;
        }

        private static Receipts.EffectEvidence ApplyFood(Operations.PawnSettingsIntent intent, Common.ObservationContext context)
        {
            var failure = ResolveFood(intent, context, out var pawn, out var policy);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var unchanged = pawn!.foodRestriction.CurrentFoodPolicy == policy;
            pawn.foodRestriction.CurrentFoodPolicy = policy;
            if (pawn.foodRestriction.CurrentFoodPolicy != policy) throw new InvalidOperationException("Native food policy requires readback.");
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.FoodRestriction,
                    Outcome = unchanged ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied } } } };
        }

        // mech_work_mode and mech_control_group: a controllable
        // mechanoid of the colony that a living colonist mechanitor oversees.
        // The mode def must exist and the group index must be one of the
        // overseer's control groups.
        private static Common.Failure? ResolveMech(Operations.PawnSettingsIntent intent, Common.ObservationContext context, out Pawn? pawn,
            out Pawn_MechanitorTracker? tracker, out MechWorkModeDef? mode, out MechanitorControlGroup? group)
        {
            pawn = null; tracker = null; mode = null; group = null;
            if (!ModsConfig.BiotechActive)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Mech settings need the Biotech DLC.");
            var wantMode = intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.MechWorkMode;
            if (wantMode)
            {
                if (!ProtoBoundary.IsIdentifier(intent.MechWorkMode))
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Mech work mode names a MechWorkModeDef.");
                mode = DefDatabase<MechWorkModeDef>.GetNamedSilentFail(intent.MechWorkMode);
                if (mode == null)
                    return ProtoBoundary.Fail(Common.FailureCode.NotFound, "No MechWorkModeDef carries this name.");
            }
            var id = intent.PawnId;
            var player = Faction.OfPlayerSilentFail;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(id);
            if (pawn == null || pawn.Dead)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living mech is not spawned on this map.");
            if (!pawn.RaceProps.IsMechanoid || !MechanitorUtility.EverControllable(pawn) || player == null || pawn.Faction != player)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Only a controllable mech of the colony has a work mode and control group.");
            var overseer = MechanitorUtility.GetOverseer(pawn);
            if (overseer == null || overseer.Dead || overseer.Faction != player || overseer.mechanitor == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The mech has no living colonist mechanitor as its overseer.");
            tracker = overseer.mechanitor;
            group = tracker.GetControlGroup(pawn);
            if (!wantMode)
            {
                var index = intent.MechControlGroup;
                if (index < 0 || index >= tracker.controlGroups.Count)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, $"The overseer has control groups 0-{tracker.controlGroups.Count - 1}.");
                group = tracker.controlGroups[index];
            }
            else if (group == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The mech belongs to no control group.");
            return null;
        }

        private static Receipts.EffectEvidence ApplyMech(Operations.PawnSettingsIntent intent, Common.ObservationContext context)
        {
            var failure = ResolveMech(intent, context, out var pawn, out var tracker, out var mode, out var group);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            bool unchanged;
            Receipts.SettingsField field;
            if (mode != null)
            {
                unchanged = group!.WorkMode == mode;
                group.SetWorkMode(mode);
                if (MechanitorUtility.GetMechWorkMode(pawn!) != mode) throw new InvalidOperationException("Native mech work mode requires readback.");
                field = Receipts.SettingsField.MechWorkMode;
            }
            else
            {
                unchanged = tracker!.GetControlGroup(pawn!) == group;
                if (!unchanged)
                {
                    tracker.UnassignPawnFromAnyControlGroup(pawn!);
                    group!.Assign(pawn!);
                }
                if (tracker.GetControlGroup(pawn!) != group) throw new InvalidOperationException("Native mech control group requires readback.");
                field = Receipts.SettingsField.MechControlGroup;
            }
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn!.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = field,
                    Outcome = unchanged ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied } } } };
        }

        // choose_permit: a free colonist on any map takes one permit of
        // a faction when the permit belongs to that faction, the colonist's
        // title reaches its minimum, its prerequisite is held and the
        // faction's permit points cover its cost. A permit already held is
        // unchanged and spends nothing.
        private static Common.Failure? ResolvePermit(Operations.PawnSettingsIntent intent, out Pawn? pawn, out Faction? faction, out RoyalTitlePermitDef? permit)
        {
            pawn = null; faction = null; permit = null;
            var choice = intent.ChoosePermit;
            if (choice == null || !choice.HasFactionDef || !ProtoBoundary.IsIdentifier(choice.FactionDef) || !choice.HasPermit || !ProtoBoundary.IsIdentifier(choice.Permit))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Choosing a permit requires a faction def and a permit def.");
            pawn = PawnsFinder.AllMaps_FreeColonists.ById(intent.PawnId);
            if (pawn == null || pawn.royalty == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact colonist with royalty is unavailable.");
            faction = Verse.Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f?.def != null && f.def.defName == choice.FactionDef);
            if (faction == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Faction is unavailable.");
            permit = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail(choice.Permit);
            if (permit == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Permit def is unavailable.");
            var royalty = pawn.royalty;
            if (royalty.HasPermit(permit, faction)) return null;
            var title = royalty.GetCurrentTitle(faction);
            if (title == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The colonist holds no title with the faction.");
            if (permit.faction != null && permit.faction != faction.def)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The permit belongs to another faction.");
            if (permit.minTitle != null && title.seniority < permit.minTitle.seniority)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The colonist's title is below the permit's minimum.");
            if (permit.prerequisite != null && !royalty.HasPermit(permit.prerequisite, faction))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The permit's prerequisite is not held.");
            if (royalty.GetPermitPoints(faction) < permit.permitPointCost)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The colonist lacks the permit points.");
            return null;
        }

        private static Receipts.EffectEvidence ApplyPermit(Operations.PawnSettingsIntent intent)
        {
            var failure = ResolvePermit(intent, out var pawn, out var faction, out var permit);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var royalty = pawn!.royalty;
            var before = royalty.GetPermitPoints(faction!);
            var unchanged = royalty.HasPermit(permit!, faction!);
            if (!unchanged) royalty.AddPermit(permit!, faction!);
            if (!royalty.HasPermit(permit!, faction!)) throw new InvalidOperationException("Native permit requires readback.");
            if (!unchanged && royalty.GetPermitPoints(faction!) != before - permit!.permitPointCost)
                throw new InvalidOperationException("Native permit points did not drop by the permit's cost.");
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.Permit,
                    Outcome = unchanged ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied } } } };
        }

        // extract_bioferrite: the entity a holding platform of the
        // map holds (a held pawn is in the platform's container, not among the
        // spawned pawns). The game's work giver offers the extraction only for
        // a true flag, and the platform's powered bioferrite harvester forces
        // the flag false every tick (CompHoldingPlatformTarget.CompTick), so
        // true is refused while a harvester is attached and until
        // BioferriteExtraction is researched (the research that unlocks the
        // order). A false flag is always accepted.
        private static Common.Failure? ResolveExtractBioferrite(Operations.PawnSettingsIntent intent, Common.ObservationContext context,
            out Pawn? pawn, out CompHoldingPlatformTarget? target)
        {
            pawn = null; target = null;
            if (!ModsConfig.AnomalyActive)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Extracting bioferrite needs the Anomaly DLC.");
            var map = ProtoBoundary.LoadedMap(context);
            pawn = map.listerBuildings.allBuildingsColonist.OfType<Building_HoldingPlatform>()
                .Select(b => b.HeldPawn).Where(p => p != null).ById(intent.PawnId);
            target = pawn?.GetComp<CompHoldingPlatformTarget>();
            if (pawn == null || pawn.Dead || target == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "No entity held on a holding platform of this map carries this id.");
            if (!intent.ExtractBioferrite) return null;
            if (!ResearchProjectDefOf.BioferriteExtraction.IsFinished)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "BioferriteExtraction is not researched.");
            if (target.HeldPlatform?.HasAttachedBioferriteHarvester == true)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The platform has an attached bioferrite harvester, which forces the flag off.");
            return null;
        }

        private static Receipts.EffectEvidence ApplyExtractBioferrite(Operations.PawnSettingsIntent intent, Common.ObservationContext context)
        {
            var failure = ResolveExtractBioferrite(intent, context, out var pawn, out var target);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var want = intent.ExtractBioferrite;
            var unchanged = target!.extractBioferrite == want;
            target.extractBioferrite = want;
            if (target.extractBioferrite != want) throw new InvalidOperationException("Native extract bioferrite requires readback.");
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn!.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.ExtractBioferrite,
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
