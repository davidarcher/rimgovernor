using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;

namespace RimGovernor.Native
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

        // Checked when a restriction is applied: paths move. Hazard safety
        // is brain policy (sheltering assigns only the roofed Safe area). Removing
        // a saved restriction restores ordinary native job reachability; it
        // never teleports a pawn.
        internal static bool AreaReachable(Pawn pawn, Area_Allowed? area)
        {
            if (area == null) return true;
            if (area.TrueCount == 0) return false;
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

        private static bool ValidArea(RimGovernor.Brain.GameOrders.Operations.Assignment? area) => area == null
            || area.Value is RimGovernor.Brain.GameOrders.Operations.AssignmentValue.ClearCase
            || (area.Value is RimGovernor.Brain.GameOrders.Operations.AssignmentValue.EntityIdCase && GameThread.IsIdentifier(area.Value?.EntityId));

        private static bool ValidSchedule(RimGovernor.Brain.GameOrders.Operations.Schedule? schedule) => schedule == null
            || (schedule.AssignmentDefs.Count == NativeWorkSettings.ScheduleHours && schedule.AssignmentDefs.All(GameThread.IsIdentifier));

        private static bool Valid(RimGovernor.Brain.GameOrders.Operations.WorkSettingsIntent? intent) => intent != null
            && intent.PawnId is not null && GameThread.IsIdentifier(intent.PawnId)
            && (intent.Work.Count > 0 || intent.AllowedArea != null || intent.Schedule != null)
                && intent.Work.All(w => w.WorkTypeDef is not null && GameThread.IsIdentifier(w.WorkTypeDef) && w.Priority >= 0 && w.Priority <= 4)
                && intent.Work.Select(w => (w.WorkTypeDef ?? "")).Distinct(StringComparer.Ordinal).Count() == intent.Work.Count
                && ValidSchedule(intent.Schedule) && ValidArea(intent.AllowedArea);

        private static bool AreaResolves(RimGovernor.Brain.GameOrders.Operations.WorkSettingsIntent intent, Pawn pawn, out Area_Allowed? area)
        {
            area = null;
            if (intent.AllowedArea == null) return true;
            if (intent.AllowedArea.Value is RimGovernor.Brain.GameOrders.Operations.AssignmentValue.ClearCase) return NativeWorkSettings.AreaReachable(pawn, null);
            area = NativeWorkSettings.ResolveArea(pawn, intent.AllowedArea.Value?.EntityId ?? "");
            return area != null && NativeWorkSettings.AreaReachable(pawn, area);
        }

        // The game's work type disabling for the pawn, by def name; an undefined type is not disabled (Resolve refuses it).
        private static Func<string, bool> Disabled(Pawn pawn) =>
            name => DefDatabase<WorkTypeDef>.GetNamedSilentFail(name) is { } def && pawn.WorkTypeIsDisabled(def);

        // Holds says whether every requested field already reads as asked. A row
        // for a type the pawn can no longer do counts as met (DisabledWorkRows).
        private static bool Holds(Pawn pawn, RimGovernor.Brain.GameOrders.Operations.WorkSettingsIntent intent)
        {
            if (intent.Work.Count > 0 && pawn.workSettings?.Initialized != true) return false;
            var disabled = Disabled(pawn);
            if (!intent.Work.All(row => {
                if (DisabledWorkRows.Skipped(row, disabled)) return true;
                var def = DefDatabase<WorkTypeDef>.GetNamedSilentFail((row.WorkTypeDef ?? ""));
                return def != null && pawn.workSettings.GetPriority(def) == row.Priority;
            })) return false;
            if (intent.Schedule != null && !NativeWorkSettings.CurrentSchedule(pawn).SequenceEqual(intent.Schedule.AssignmentDefs, StringComparer.Ordinal)) return false;
            if (intent.AllowedArea == null) return true;
            var current = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
            if (intent.AllowedArea.Value is RimGovernor.Brain.GameOrders.Operations.AssignmentValue.ClearCase) return current == null;
            return current != null && (RefIndex.Is(current, intent.AllowedArea.Value?.EntityId)
                || current.ID.ToString(CultureInfo.InvariantCulture) == intent.AllowedArea.Value?.EntityId);
        }

        // Resolve finds the pawn and returns null when the intent applies:
        // either every field already holds, or each apply-time rule
        // (action-contracts.md) passes.
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.WorkSettingsIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out bool holds)
        {
            pawn = null; holds = false;
            if (intent is null || !Valid(intent))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn settings require an exact pawn and a supported settings change.");
            var found = GameThread.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            bool Target(Pawn p) => !p.Dead && p.IsFreeColonist;
            if (found != null && Target(found) && Holds(found, intent)) { pawn = found; holds = true; return null; }
            var manual = PawnSettingsRead.ManualPriorities();
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && GameThread.IsLoaded(found.Map), "the exact pawn is no longer spawned on this map")
                .Require(() => found != null && (Target(found)), "the pawn is not a living free colonist");
            rules.Require(() => found != null && (!found.Downed), "the pawn is downed")
                    .Require(() => found != null && (!found.Drafted), "the pawn is drafted")
                    .Require(() => found != null && (!found.InMentalState), "the pawn is in a mental state")
                    .Require(() => found != null && (found.workSettings?.Initialized == true && found.workSettings.EverWork), "the pawn has no work settings")
                    .Require(() => manual.HasValue, "the game's manual-priorities setting is unreadable")
                    .Require(() => intent.Work.All(row => DefDatabase<WorkTypeDef>.GetNamedSilentFail((row.WorkTypeDef ?? "")) != null), "a requested work type is not defined")
                    .Require(() => manual.GetValueOrDefault() || intent.Work.All(row => row.Priority == 0 || row.Priority == 3), "manual priorities are off, so only 0 or 3 can be set")
                    .Require(() => found != null && (AreaResolves(intent, found, out _)), "the requested allowed area is missing or unreachable")
                    .Require(() => intent.Schedule == null || intent.Schedule.AssignmentDefs.All(name => DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(name) != null), "a requested timetable assignment is not defined")
                    .Require(() => found != null && (intent.Schedule == null || found.timetable?.times != null && found.timetable.times.Count == NativeWorkSettings.ScheduleHours), "the pawn has no 24-hour timetable");
            if (!rules.Holds || found == null) return rules.Failure();
            pawn = found;
            return null;
        }

        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(action.Intent?.WorkSettings, context, out _, out _);

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var intent = action.Intent?.WorkSettings;
            var failure = Resolve(intent, context, out var pawn, out var holds);
            if (failure != null || intent is null || pawn == null) throw new InvalidOperationException("Work settings prerequisites changed before apply: " + (failure?.Detail ?? ""));
            var disabled = Disabled(pawn);
            if (!holds)
            {
                foreach (var row in intent.Work.Where(row => !DisabledWorkRows.Skipped(row, disabled))) pawn.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed(row.WorkTypeDef ?? ""), row.Priority);
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
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = Evidence(intent, DisabledWorkRows.SkippedNames(intent.Work, disabled)) };
        }

        private static RimGovernor.Brain.GameFacts.Receipts.SettingsEffect Evidence(RimGovernor.Brain.GameOrders.Operations.WorkSettingsIntent intent, System.Collections.Generic.List<string> skipped)
        {
            var fields = new System.Collections.Generic.List<RimGovernor.Brain.GameFacts.Receipts.FieldResult>();
            if (intent.Work.Count > 0) fields.Add(new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.Work, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied });
            if (intent.AllowedArea != null) fields.Add(new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.AllowedArea, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied });
            if (intent.Schedule != null) fields.Add(new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.Schedule, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied });
            return new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect { Fields = [.. fields], SkippedWork = [.. skipped] };
        }
    }
}
