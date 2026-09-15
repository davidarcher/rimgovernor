#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed SetProductionPolicy/ReadProductionPolicy. This is an idempotent
    // set-and-verify write, not a multi-step attempt like construction or a bill:
    // the whole policy (floors/commitments/stopped/drills) replaces the map-scoped
    // ProductionPolicyState in one native call and is immediately verified by
    // re-reading it back, exactly like NativeWorkSettings' pawn-scoped priorities.
    // It still goes through the shared attempt/lease/ledger admission every
    // rimgovernor/operations_execute command requires (enforced once, up front,
    // by NativeOperationTools.ExecuteNative for every operation kind) so retries
    // of the same attempt replay their original receipt instead of re-applying.
    // The underlying RimWorld manipulation (floors/commitments/stopped bookkeeping,
    // drill ownership, interrupting active bill jobs on change) is the exact logic
    // ProductionPolicyTool.Policy's dry-run=false branch already uses; this is the
    // typed proto-shaped entry point onto the same ProductionPolicyGuard/
    // DrillingGuard state, not a reimplementation.
    internal static class NativeProductionPolicyOperations
    {
        private static void EnsureReady() { ProductionPolicyGuard.EnsurePatched(); DrillingGuard.Install(); }
        private static string Prefix(int mapId) => mapId + ":";

        internal static bool ValidCounts(Operations.DefCounts? counts, out Dictionary<string, int> parsed)
        {
            parsed = new Dictionary<string, int>();
            if (counts == null || counts.Rows.Count > 256) return false;
            foreach (var row in counts.Rows)
            {
                if (!row.HasDefName || !row.HasCount || row.Count < 0
                    || DefDatabase<ThingDef>.GetNamedSilentFail(row.DefName) == null || parsed.ContainsKey(row.DefName))
                    return false;
                parsed[row.DefName] = row.Count;
            }
            return true;
        }

        internal static bool ValidStopped(Operations.DefinitionList? list, out List<string> parsed)
        {
            parsed = new List<string>();
            if (list == null || list.Defs.Count > 256) return false;
            foreach (var name in list.Defs)
            {
                if (DefDatabase<ThingDef>.GetNamedSilentFail(name) == null || parsed.Contains(name)) return false;
                parsed.Add(name);
            }
            return true;
        }

        // Reuses DrillingGuard.Parse's exact tested validation (bounded ownership,
        // no adopting a player drill, retained-facility inspection requirement) by
        // rebuilding its CSV shape from the typed rows instead of duplicating it.
        internal static bool ValidDrills(Map map, Operations.DrillPolicies? drills, out List<DrillingRecord> parsed, out Common.Failure? failure)
        {
            parsed = new List<DrillingRecord>();
            failure = null;
            if (drills == null || drills.Rows.Count > 32)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Invalid or excessive drilling facility list.");
                return false;
            }
            var csv = string.Join(",", drills.Rows.Select(r => r.BuildingDef + "/" + (r.Cell?.X ?? 0) + "/" + (r.Cell?.Z ?? 0) + "/" + r.ResourceDef + "/" + r.StockTarget));
            try { parsed = DrillingGuard.Parse(map, csv); }
            catch (ArgumentException error) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, error.Message); return false; }
            return true;
        }

        internal static Obs.ProductionPolicySnapshot Snapshot(Map map, Common.ObservationContext context)
        {
            var state = ProductionPolicyGuard.State();
            var prefix = Prefix(map.uniqueID);
            var snapshot = new Obs.ProductionPolicySnapshot { CommitmentsActive = Supervisor.IsActive };
            foreach (var floor in state.Floors.Where(p => p.Key.StartsWith(prefix)).OrderBy(p => p.Key, StringComparer.Ordinal))
                snapshot.Floors.Add(new Obs.Quantity { DefName = floor.Key.Substring(prefix.Length), Units = floor.Value });
            foreach (var held in state.Commitments.Where(p => p.Key.StartsWith(prefix)).OrderBy(p => p.Key, StringComparer.Ordinal))
                snapshot.Commitments.Add(new Obs.Quantity { DefName = held.Key.Substring(prefix.Length), Units = held.Value });
            foreach (var stopped in state.Stopped.Where(p => p.StartsWith(prefix)).OrderBy(p => p, StringComparer.Ordinal))
                snapshot.StoppedDefs.Add(stopped.Substring(prefix.Length));
            foreach (var drill in MiningGuard.State().Drills.Where(r => r.MapId == map.uniqueID).OrderBy(r => r.X).ThenBy(r => r.Z))
            {
                var row = new Obs.OwnedDrill { DefName = drill.Definition, Cell = new Common.Cell { X = drill.X, Z = drill.Z },
                    Resource = drill.Resource, Recovered = drill.Recovered, StockTarget = drill.Target, Missing = drill.ThingId == null };
                if (drill.ThingId != null) row.ThingId = drill.ThingId;
                if (drill.PendingId != null) row.PendingId = drill.PendingId;
                row.Snapshot = NativeObservationSnapshot.Snapshot("production-policy-drill", context, drill.MapId + ":" + drill.X + ":" + drill.Z,
                    w => { w.Write(drill.Definition ?? ""); w.Write(drill.Resource ?? ""); w.Write(drill.Target); w.Write(drill.Recovered); w.Write(drill.ThingId ?? ""); });
                snapshot.Drills.Add(row);
            }
            snapshot.Snapshot = NativeObservationSnapshot.Snapshot("production-policy", context, map.uniqueID.ToString(), w =>
            {
                foreach (var row in snapshot.Floors) { w.Write(row.DefName ?? ""); w.Write(row.Units); }
                foreach (var row in snapshot.Commitments) { w.Write(row.DefName ?? ""); w.Write(row.Units); }
                foreach (var def in snapshot.StoppedDefs) w.Write(def);
                foreach (var drill in snapshot.Drills) { w.Write(drill.DefName ?? ""); w.Write(drill.Cell.X); w.Write(drill.Cell.Z); w.Write(drill.Resource ?? ""); w.Write(drill.StockTarget); }
                w.Write(snapshot.CommitmentsActive);
            });
            var total = snapshot.Floors.Count + snapshot.Commitments.Count + snapshot.StoppedDefs.Count + snapshot.Drills.Count;
            snapshot.Completeness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true },
                Matched = (ulong)total, Returned = (ulong)total, Filtered = 0, Unreadable = 0 };
            return snapshot;
        }

        private static bool Prepare(Operations.SetProductionPolicy? command, Common.ObservationContext context, Map? map,
            out Dictionary<string, int> floors, out Dictionary<string, int> commitments, out List<string> stopped, out List<DrillingRecord> drills,
            out Common.Failure failure)
        {
            floors = new Dictionary<string, int>(); commitments = new Dictionary<string, int>(); stopped = new List<string>(); drills = new List<DrillingRecord>();
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Production policy requires complete floors/commitments/stopped/drills replacement rows and a matching current snapshot token.");
            if (command == null || map == null || !command.HasExpectedSnapshotToken) return false;
            if (!ValidCounts(command.Floors, out floors)) return false;
            if (!ValidCounts(command.Commitments, out commitments)) return false;
            if (!ValidStopped(command.StoppedDefs, out stopped)) return false;
            if (!ValidDrills(map, command.Drills, out drills, out var drillFailure)) { if (drillFailure != null) failure = drillFailure; return false; }
            var current = Snapshot(map, context);
            if (current.Snapshot.Token != command.ExpectedSnapshotToken) return false;
            return true;
        }

        // True when the live native state already equals what `command` requested,
        // independent of any particular attempt's before/after tokens. Used both to
        // decide whether Execute actually changes anything and to answer a later
        // Observe/progress poll purely by re-reading current state.
        private static bool Matches(Map map, Operations.SetProductionPolicy command)
        {
            if (!ValidCounts(command.Floors, out var floors) || !ValidCounts(command.Commitments, out var commitments)
                || !ValidStopped(command.StoppedDefs, out var stopped)) return false;
            var state = ProductionPolicyGuard.State(); var prefix = Prefix(map.uniqueID);
            var curFloors = state.Floors.Where(p => p.Key.StartsWith(prefix)).ToDictionary(p => p.Key.Substring(prefix.Length), p => p.Value);
            var curHolds = state.Commitments.Where(p => p.Key.StartsWith(prefix)).ToDictionary(p => p.Key.Substring(prefix.Length), p => p.Value);
            var curStops = state.Stopped.Where(p => p.StartsWith(prefix)).Select(p => p.Substring(prefix.Length)).OrderBy(p => p, StringComparer.Ordinal);
            if (curFloors.Count != floors.Count || !curFloors.All(p => floors.TryGetValue(p.Key, out var n) && n == p.Value)) return false;
            if (curHolds.Count != commitments.Count || !curHolds.All(p => commitments.TryGetValue(p.Key, out var n) && n == p.Value)) return false;
            if (!curStops.SequenceEqual(stopped.OrderBy(p => p, StringComparer.Ordinal))) return false;
            var requestedDrills = (command.Drills?.Rows ?? new Google.Protobuf.Collections.RepeatedField<Operations.DrillPolicy>())
                .Select(r => (Def: r.BuildingDef, X: r.Cell?.X ?? 0, Z: r.Cell?.Z ?? 0, Target: r.StockTarget)).ToList();
            var recordedDrills = MiningGuard.State().Drills.Where(r => r.MapId == map.uniqueID && r.Target > 0).ToList();
            if (recordedDrills.Count != requestedDrills.Count) return false;
            return requestedDrills.All(expected => recordedDrills.Any(r => r.X == expected.X && r.Z == expected.Z
                && r.Definition == expected.Def && r.Target == expected.Target));
        }

        // Mirrors ProductionPolicyTool.Policy's dry-run=false branch: replace the
        // map-scoped floors/commitments/stopped rows, apply drill targets, and
        // interrupt in-progress bill jobs whenever floors/stopped change (or
        // commitments change/supervision is active), returning interrupted pawn ids.
        private static (List<string> Interrupted, bool Changed) Apply(Map map,
            Dictionary<string, int> floors, Dictionary<string, int> commitments, List<string> stopped, List<DrillingRecord> drills)
        {
            var state = ProductionPolicyGuard.State(); var prefix = Prefix(map.uniqueID);
            var priorFloors = state.Floors.Where(p => p.Key.StartsWith(prefix)).ToDictionary(p => p.Key.Substring(prefix.Length), p => p.Value);
            var priorStops = state.Stopped.Where(p => p.StartsWith(prefix)).Select(p => p.Substring(prefix.Length)).OrderBy(p => p, StringComparer.Ordinal);
            var priorHolds = state.Commitments.Where(p => p.Key.StartsWith(prefix)).ToDictionary(p => p.Key.Substring(prefix.Length), p => p.Value);
            var heldChanged = priorHolds.Count != commitments.Count || priorHolds.Any(p => !commitments.TryGetValue(p.Key, out var n) || n != p.Value);
            var changed = priorFloors.Count != floors.Count || priorFloors.Any(p => !floors.TryGetValue(p.Key, out var n) || n != p.Value)
                || !priorStops.SequenceEqual(stopped.OrderBy(p => p, StringComparer.Ordinal));
            DrillingGuard.Apply(map, drills);
            var interrupted = new List<string>();
            if (changed || heldChanged)
            {
                foreach (var key in state.Floors.Keys.Where(k => k.StartsWith(prefix)).ToList()) state.Floors.Remove(key);
                foreach (var key in state.Commitments.Keys.Where(k => k.StartsWith(prefix)).ToList()) state.Commitments.Remove(key);
                foreach (var pair in commitments) state.Commitments[prefix + pair.Key] = pair.Value;
                state.Stopped.RemoveAll(k => k.StartsWith(prefix));
                foreach (var pair in floors) state.Floors[prefix + pair.Key] = pair.Value;
                state.Stopped.AddRange(stopped.Select(s => prefix + s));
                if (changed || Supervisor.IsActive)
                    foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p.CurJob?.bill != null).ToList())
                    { interrupted.Add(pawn.ThingID); pawn.jobs.EndCurrentJob(JobCondition.InterruptForced); }
            }
            return (interrupted, changed || heldChanged);
        }

        internal static Operations.PreviewReply Preview(Operations.SetProductionPolicy command, Common.ObservationContext context)
        {
            try
            {
                EnsureReady();
                if (!Prepare(command, context, Find.CurrentMap, out _, out _, out _, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Production policy preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.SetProductionPolicy;
            var map = Find.CurrentMap;
            try
            {
                EnsureReady();
                if (!Prepare(command, context, map, out var floors, out var commitments, out var stopped, out var drills, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!;
                handle = admitted.Handle;
                // Track even partial application before mutating; a setter failure cannot erase a write.
                state.ProductionPolicies.Add(pre.Attempt.Clone(), command.Clone());
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, map, out var checkedFloors, out var checkedCommitments, out var checkedStopped, out var checkedDrills, out failure))
                        throw new InvalidOperationException("Production policy admission changed before effect.");
                    var before = Snapshot(map!, context);
                    var (interrupted, changed) = Apply(map!, checkedFloors, checkedCommitments, checkedStopped, checkedDrills);
                    var after = Snapshot(map!, context);
                    var effect = new Receipts.ProductionPolicyEffect { Snapshot = new Receipts.SnapshotEvidence {
                        EntityId = map!.uniqueID.ToString(), BeforeToken = before.Snapshot.Token, AfterToken = after.Snapshot.Token }, Changed = changed };
                    effect.InterruptedPawnIds.AddRange(interrupted);
                    evidence = new Receipts.EffectEvidence { ProductionPolicy = effect };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Production policy admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted production policy requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Operations.SetProductionPolicy command)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact production policy state is unavailable." } };
            try
            {
                var map = Find.CurrentMap;
                if (map == null) return result;
                EnsureReady();
                var current = Snapshot(map, context);
                var matches = Matches(map, command);
                var effect = new Receipts.ProductionPolicyEffect { Snapshot = new Receipts.SnapshotEvidence {
                    EntityId = map.uniqueID.ToString(), BeforeToken = command.ExpectedSnapshotToken, AfterToken = current.Snapshot.Token } };
                var evidence = new Receipts.EffectEvidence { ProductionPolicy = effect };
                result.CompleteInspection = true;
                if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = "Current production policy differs from the requested floors/commitments/stopped/drills; do not restore over player or other-owner changes." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
