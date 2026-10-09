#nullable enable
using System;
using System.Globalization;
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
    // The pawn settings snapshot observations publish: work, area, schedule
    // and food under one token.
    internal static class NativeWorkSettings
    {
        internal const int ScheduleHours = 24;

        internal static string[] CurrentSchedule(Pawn pawn) => pawn.timetable?.times?.Select(t => t?.defName ?? "").ToArray() ?? new string[0];

        // Resolves a requested area identifier tolerantly against either the
        // GetUniqueLoadID() scheme published by observations_list_pawns'
        // allowed_area_id, or the Area.ID integer scheme
        // NativeRecoveryFacts's roofed-refuge census publishes.
        internal static Area_Allowed? ResolveArea(Pawn pawn, string entityId) => pawn.Map?.areaManager.AllAreas.OfType<Area_Allowed>()
            .FirstOrDefault(a => RefIndex.Is(a, entityId) || a.ID.ToString(CultureInfo.InvariantCulture) == entityId);

        // Checked when a restriction is applied: weather, roof geometry and
        // paths move. Removing a saved restriction restores ordinary native
        // job reachability; it never teleports a pawn.
        internal static bool AreaSafeAndReachable(Pawn pawn, Area_Allowed? area)
        {
            var conditions = new System.Collections.Generic.List<GameCondition>();
            pawn.Map.gameConditionManager.GetAllGameConditionsAffectingMap(pawn.Map, conditions);
            var roofHazard = conditions.Any(c => c is GameCondition_ToxicFallout);
            if (area == null) return !roofHazard;
            if (area.TrueCount == 0 || (roofHazard && area.ActiveCells.Any(c => !c.Roofed(pawn.Map) || c.Fogged(pawn.Map)))) return false;
            return area.ActiveCells.Any(c => !c.Fogged(pawn.Map) && c.Standable(pawn.Map)
                && pawn.CanReach(c, Verse.AI.PathEndMode.OnCell, Danger.Some));
        }
    }

    // WorkSettingsIntent: one free colonist's work priorities, allowed
    // area and timetable together. Native checks the pawn and
    // each field live when it applies; settings that already hold apply again.
    internal sealed class WorkSettingsActionHandler : IActionHandler
    {
        internal const string Kind = "Work settings";

        internal WorkSettingsActionHandler() { NativeDrugPolicy.Install(); }

        private static bool ValidArea(Operations.Assignment? area) => area == null
            || area.ValueCase == Operations.Assignment.ValueOneofCase.Clear
            || (area.ValueCase == Operations.Assignment.ValueOneofCase.EntityId && ProtoBoundary.IsIdentifier(area.EntityId));

        private static bool ValidSchedule(Operations.Schedule? schedule) => schedule == null
            || (schedule.AssignmentDefs.Count == NativeWorkSettings.ScheduleHours && schedule.AssignmentDefs.All(ProtoBoundary.IsIdentifier));

        private static bool Valid(Operations.WorkSettingsIntent? intent) => intent != null
            && intent.HasPawnId && ProtoBoundary.IsIdentifier(intent.PawnId)
            && (intent.Work.Count > 0 || intent.AllowedArea != null || intent.Schedule != null)
                && intent.Work.All(w => w.HasWorkTypeDef && ProtoBoundary.IsIdentifier(w.WorkTypeDef) && w.HasPriority && w.Priority >= 0 && w.Priority <= 4)
                && intent.Work.Select(w => w.WorkTypeDef).Distinct(StringComparer.Ordinal).Count() == intent.Work.Count
                && ValidSchedule(intent.Schedule) && ValidArea(intent.AllowedArea);

        private static bool AreaResolves(Operations.WorkSettingsIntent intent, Pawn pawn, out Area_Allowed? area)
        {
            area = null;
            if (intent.AllowedArea == null) return true;
            if (intent.AllowedArea.ValueCase == Operations.Assignment.ValueOneofCase.Clear) return NativeWorkSettings.AreaSafeAndReachable(pawn, null);
            area = NativeWorkSettings.ResolveArea(pawn, intent.AllowedArea.EntityId);
            return area != null && NativeWorkSettings.AreaSafeAndReachable(pawn, area);
        }

        // Holds says whether every requested field already reads as asked.
        private static bool Holds(Pawn pawn, Operations.WorkSettingsIntent intent)
        {
            if (intent.Work.Count > 0 && pawn.workSettings?.Initialized != true) return false;
            if (!intent.Work.All(row => {
                var def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(row.WorkTypeDef);
                return def != null && pawn.workSettings.GetPriority(def) == row.Priority;
            })) return false;
            if (intent.Schedule != null && !NativeWorkSettings.CurrentSchedule(pawn).SequenceEqual(intent.Schedule.AssignmentDefs, StringComparer.Ordinal)) return false;
            if (intent.AllowedArea == null) return true;
            var current = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
            if (intent.AllowedArea.ValueCase == Operations.Assignment.ValueOneofCase.Clear) return current == null;
            return current != null && (RefIndex.Is(current, intent.AllowedArea.EntityId)
                || current.ID.ToString(CultureInfo.InvariantCulture) == intent.AllowedArea.EntityId);
        }

        // Resolve finds the pawn and returns null when the intent applies:
        // either every field already holds, or each apply-time rule
        // (action-contracts.md) passes.
        private static Common.Failure? Resolve(Operations.WorkSettingsIntent? intent, Common.ObservationContext context, out Pawn pawn, out bool holds)
        {
            pawn = null!; holds = false;
            if (!Valid(intent))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn settings require an exact pawn and a supported settings change.");
            var found = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent!.PawnId);
            bool Target(Pawn p) => !p.Dead && p.IsFreeColonist;
            if (found != null && Target(found) && Holds(found, intent!)) { pawn = found; holds = true; return null; }
            var manual = PawnSettingsRead.ManualPriorities();
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact pawn is no longer spawned on this map")
                .Require(() => Target(found!), "the pawn is not a living free colonist");
            rules.Require(() => !found!.Downed, "the pawn is downed")
                    .Require(() => !found!.Drafted, "the pawn is drafted")
                    .Require(() => !found!.InMentalState, "the pawn is in a mental state")
                    .Require(() => found!.workSettings?.Initialized == true && found.workSettings.EverWork, "the pawn has no work settings")
                    .Require(() => manual.HasValue, "the game's manual-priorities setting is unreadable")
                    .Require(() => intent.Work.All(row => DefDatabase<WorkTypeDef>.GetNamedSilentFail(row.WorkTypeDef) != null), "a requested work type is not defined")
                    .Require(() => intent.Work.All(row => row.Priority == 0 || !found!.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed(row.WorkTypeDef))), "a requested work type is disabled for the pawn")
                    .Require(() => manual.GetValueOrDefault() || intent.Work.All(row => row.Priority == 0 || row.Priority == 3), "manual priorities are off, so only 0 or 3 can be set")
                    .Require(() => AreaResolves(intent, found!, out _), "the requested allowed area is missing, unreachable or unsafe under the current roof hazard")
                    .Require(() => intent.Schedule == null || intent.Schedule.AssignmentDefs.All(name => DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(name) != null), "a requested timetable assignment is not defined")
                    .Require(() => intent.Schedule == null || found!.timetable?.times != null && found.timetable.times.Count == NativeWorkSettings.ScheduleHours, "the pawn has no 24-hour timetable");
            if (!rules.Holds) return rules.Failure();
            pawn = found!;
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.WorkSettings, context, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.WorkSettings;
            var failure = Resolve(intent, context, out var pawn, out var holds);
            if (failure != null) throw new InvalidOperationException("Work settings prerequisites changed before apply: " + failure.Detail);
            if (!holds)
            {
                foreach (var row in intent.Work) pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed(row.WorkTypeDef), row.Priority);
                if (intent.AllowedArea != null)
                {
                    if (!AreaResolves(intent, pawn, out var area)) throw new InvalidOperationException("Requested allowed area is no longer resolvable.");
                    pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                }
                if (intent.Schedule != null)
                    for (var hour = 0; hour < NativeWorkSettings.ScheduleHours; hour++)
                        pawn.timetable.SetAssignment(hour, DefDatabase<TimeAssignmentDef>.GetNamed(intent.Schedule.AssignmentDefs[hour]));
                if (!Holds(pawn, intent)) throw new InvalidOperationException("Native work settings did not take effect.");
            }
            return new Receipts.EffectEvidence { Settings = Evidence(intent) };
        }

        private static Receipts.SettingsEffect Evidence(Operations.WorkSettingsIntent intent)
        {
            var effect = new Receipts.SettingsEffect { Snapshot = new Receipts.SnapshotEvidence { EntityId = intent.PawnId } };
            void Add(Receipts.SettingsField field) => effect.Fields.Add(new Receipts.FieldResult { Field = field, Outcome = Receipts.FieldOutcome.Applied });
            foreach (var row in intent.Work) effect.Fields.Add(new Receipts.FieldResult { Field = Receipts.SettingsField.Work,
                WorkTypeDef = row.WorkTypeDef, Outcome = Receipts.FieldOutcome.Applied });
            if (intent.AllowedArea != null) Add(Receipts.SettingsField.AllowedArea);
            if (intent.Schedule != null) Add(Receipts.SettingsField.Schedule);
            return effect;
        }
    }
}
