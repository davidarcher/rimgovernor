#nullable enable
using System;
using System.Collections.Generic;
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
    // Staged excavation read: certifies a requested rock cell set (a stage or a
    // whole target) for ordinary pawn mining. Roofed cells are admissible; the
    // site-level support answer is a counterfactual over all requested cells.
    internal static class NativeExcavationSite
    {
        internal const int MaxCells = 64;
        internal static bool Validate(Obs.ExcavationSiteRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity, 1..64 unique nonnegative cells and an access cell required.");
            if (request?.Scope?.ExpectedIdentity == null || request.Cells.Count < 1 || request.Cells.Count > MaxCells || !HasCell(request.AccessCell)) return false;
            var seen = new HashSet<(int, int)>();
            foreach (var cell in request.Cells) if (!HasCell(cell) || !seen.Add((cell.X, cell.Z))) return false;
            return true;
        }
        private static bool HasCell(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ && cell.X >= 0 && cell.Z >= 0;

        internal static string Token(Common.Identity identity, IntVec3 cell, string definition, int hitPoints, bool designated)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                { writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId); writer.Write(cell.x); writer.Write(cell.z); writer.Write(definition); writer.Write(hitPoints); writer.Write(designated); }
                using (var hash = SHA256.Create()) return "excavate-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.ExcavationCell Row(IntVec3 cell, Map map, Common.ObservationContext context)
        {
            var row = new Obs.ExcavationCell { Cell = new Common.Cell { X = cell.x, Z = cell.z }, Fogged = false, MineDesignated = false, Eligible = false };
            if (!cell.InBounds(map)) { row.Eligible = false; row.Blocker = "Cell is outside the map"; return row; }
            row.Fogged = cell.Fogged(map);
            if (row.Fogged) { row.Eligible = false; row.Blocker = "Unknown excavation geometry"; return row; }
            var roof = cell.GetRoof(map);
            if (roof != null) row.RoofDefName = roof.defName;
            row.Walkable = cell.Walkable(map);
            var edifice = cell.GetEdifice(map);
            row.HoldsRoof = edifice != null && edifice.def.holdsRoof;
            row.MineDesignated = ExcavationTools.Designated(cell, map);
            var rock = ExcavationTools.RockAt(cell, map);
            if (rock != null)
            {
                row.MineableDefName = rock.def.defName; row.MineableId = rock.GetUniqueLoadID(); row.HitPoints = rock.HitPoints;
                row.Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = rock.GetUniqueLoadID(),
                    Token = Token(context.Identity, cell, rock.def.defName, rock.HitPoints, row.MineDesignated) };
            }
            else if (row.Walkable)
            {
                // Already cleared (pawns finished a designation the controller
                // no longer tracks): the snapshot lets an excavation of this
                // cell be adopted as done rather than held as changed geometry.
                row.Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = "cleared", Token = Token(context.Identity, cell, "", 0, row.MineDesignated) };
            }
            var blocker = ExcavationTools.CellBlocker(cell, map);
            row.Eligible = blocker == null && ExcavationTools.Eligible(cell, map);
            if (blocker != null) row.Blocker = blocker;
            else if (!row.Eligible) row.Blocker = "Native mining designation is not accepted at this cell";
            return row;
        }

        internal static Obs.ExcavationSiteSnapshot Read(Map map, Obs.ExcavationSiteRequest request, Common.ObservationContext context)
        {
            var cells = request.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToList();
            var access = new IntVec3(request.AccessCell.X, 0, request.AccessCell.Z);
            var snapshot = new Obs.ExcavationSiteSnapshot { Context = context, CollapsePending = map.roofCollapseBuffer.CellsMarkedToCollapse.Count > 0 };
            foreach (var cell in cells) snapshot.Cells.Add(Row(cell, map, context));
            var known = cells.Where(c => c.InBounds(map) && !c.Fogged(map)).ToList();
            if (known.Count == 0) { snapshot.SupportAfterRemoval = Obs.ExcavationSupport.Unknown; snapshot.SupportBlocker = "Unknown excavation geometry"; }
            else
            {
                var support = ExcavationSafety.Check(map, known, out var checkedRoofs, out var blocker);
                snapshot.RoofCellsChecked = (uint)checkedRoofs;
                snapshot.SupportAfterRemoval = support == ExcavationSafety.Support.Supported ? Obs.ExcavationSupport.Supported
                    : support == ExcavationSafety.Support.Unknown ? Obs.ExcavationSupport.Unknown : Obs.ExcavationSupport.Unsupported;
                if (blocker != null) snapshot.SupportBlocker = blocker;
                if (known.Count < cells.Count && snapshot.SupportAfterRemoval == Obs.ExcavationSupport.Supported)
                { snapshot.SupportAfterRemoval = Obs.ExcavationSupport.Unknown; snapshot.SupportBlocker = "Fogged cells in the requested set are unknown"; }
            }
            if (snapshot.CollapsePending)
            { snapshot.SupportAfterRemoval = Obs.ExcavationSupport.Unsupported; snapshot.SupportBlocker = "Roof collapse is already pending"; }
            // The access cell may itself be rock (single-cell dispatch reads pass
            // the target): a miner stands on any visible walkable neighbour.
            var stand = ExcavationTools.StandingCell(access, map);
            snapshot.AccessReachable = stand.IsValid;
            var workers = snapshot.AccessReachable ? ExcavationTools.Workers(map, stand) : new List<Pawn>();
            snapshot.WorkerAvailable = workers.Count > 0;
            foreach (var worker in workers.Take(32)) snapshot.WorkerIds.Add(worker.GetUniqueLoadID());
            snapshot.Completeness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)cells.Count, Returned = (ulong)cells.Count, Filtered = 0, Unreadable = 0 };
            return snapshot;
        }
    }

    // Excavation completes when no rock remains at the cell; output stacks are
    // not evidence. A pre-existing Mine designation is adopted rather than
    // refused so a restarted controller can re-admit the same cell.
    internal sealed class NativeExcavationRecord
    {
        private readonly Map map;
        private readonly IntVec3 cell;
        private readonly string definition;
        private readonly bool adopted;
        internal NativeExcavationRecord(Map map, IntVec3 cell, string definition, bool adopted)
        { this.map = map; this.cell = cell; this.definition = definition; this.adopted = adopted; }
        internal Receipts.ExcavationEffect Evidence()
        {
            var rock = ExcavationTools.RockAt(cell, map);
            var result = new Receipts.ExcavationEffect { Cell = new Common.Cell { X = cell.x, Z = cell.z }, MineableDefName = definition,
                AdoptedExistingDesignation = adopted, Cleared = rock == null, Designated = rock != null && ExcavationTools.Designated(cell, map) };
            result.Cancelled = rock != null && !result.Designated;
            var record = MiningGuard.State().Excavations.LastOrDefault(r => r.MapId == map.uniqueID && r.X == cell.x && r.Z == cell.z);
            if (record?.Blocker != null) result.Blocker = record.Blocker;
            return result;
        }
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            if (map != ProtoBoundary.ResolveMap(context)) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Excavation map is not the current map." }; return result; }
            var observed = Evidence();
            var evidence = new Receipts.EffectEvidence { Excavation = observed };
            if (observed.Cleared) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else if (observed.Designated) result.Pending = new Receipts.PendingEffect { Evidence = evidence };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                Detail = observed.HasBlocker ? observed.Blocker : "Excavation designation is gone and the rock remains." };
            return result;
        }
    }

    internal static class NativeExcavationOperations
    {
        internal static bool Valid(Operations.ExcavateCell? command) => command != null && command.Cell != null && command.Cell.HasX && command.Cell.HasZ
            && command.Cell.X >= 0 && command.Cell.Z >= 0 && command.HasExpectedMineableDefName && ProtoBoundary.IsIdentifier(command.ExpectedMineableDefName)
            && command.HasExpectedSnapshotToken && command.ExpectedSnapshotToken.Length > 0;

        private static bool Prepare(Operations.ExcavateCell command, Common.ObservationContext context, out Mineable? rock, out bool cleared, out Common.Failure failure)
        {
            rock = null; cleared = false; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Excavation requires an exact visible rock cell snapshot, supported roof geometry and an eligible miner.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Paused current map required."); return false; }
            if (map.roofCollapseBuffer.CellsMarkedToCollapse.Count > 0) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Roof collapse is pending on this map."); return false; }
            var cell = new IntVec3(command.Cell.X, 0, command.Cell.Z);
            rock = ExcavationTools.RockAt(cell, map);
            if (rock == null && cell.InBounds(map) && !cell.Fogged(map) && cell.Walkable(map))
            {
                // Adopt a cell the pawns already cleared: the snapshot must
                // still describe open ground, and nothing is designated.
                if (NativeExcavationSite.Token(context.Identity, cell, "", 0, ExcavationTools.Designated(cell, map)) != command.ExpectedSnapshotToken)
                { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Cell snapshot changed; observe before new admission."); return false; }
                cleared = true;
                return true;
            }
            if (rock == null || cell.Fogged(map) || rock.def.defName != command.ExpectedMineableDefName) { rock = null; failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Expected rock is not visible at the cell."); return false; }
            var designated = ExcavationTools.Designated(cell, map);
            if (NativeExcavationSite.Token(context.Identity, cell, rock.def.defName, rock.HitPoints, designated) != command.ExpectedSnapshotToken)
            { rock = null; failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Rock snapshot changed; observe before new admission."); return false; }
            var blocker = ExcavationTools.CellBlocker(cell, map);
            if (blocker == null && ExcavationSafety.Check(map, new[] { cell }, out _, out var support) != ExcavationSafety.Support.Supported) blocker = support;
            if (blocker == null && !designated && !new Designator_Mine().CanDesignateCell(cell).Accepted) blocker = "Native mining designation is not accepted at this cell";
            if (blocker != null) { rock = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, blocker); return false; }
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.ExcavateCell command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.ExcavateCell;
            try
            {
                if (!Prepare(command, context, out var rock, out var cleared, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                var map = ProtoBoundary.LoadedMap(context); var cell = new IntVec3(command.Cell.X, 0, command.Cell.Z);
                if (cleared)
                {
                    // Nothing to designate: the receipt carries the cleared
                    // evidence and observation completes the action.
                    var done = new NativeExcavationRecord(map, cell, command.ExpectedMineableDefName, false);
                    state.Excavation.Add(pre.Attempt.Clone(), done);
                    evidence = new Receipts.EffectEvidence { Excavation = done.Evidence() };
                    if (!evidence.Excavation.Cleared) throw new InvalidOperationException("Cleared excavation cell was not observed.");
                    return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
                }
                var adopted = ExcavationTools.Designated(cell, map);
                var record = new NativeExcavationRecord(map, cell, rock!.def.defName, adopted);
                state.Excavation.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out var checkedRock, out _, out failure) || !ReferenceEquals(rock, checkedRock)) throw new InvalidOperationException("Excavation target changed before designation.");
                    if (MiningGuard.OpenExcavation(map, cell) == null)
                        MiningGuard.State().Excavations.Add(new ExcavationRecord { MapId = map.uniqueID, X = cell.x, Z = cell.z, Definition = rock.def.defName, Started = Find.TickManager.TicksGame });
                    if (!adopted) new Designator_Mine().DesignateSingleCell(cell);
                    evidence = new Receipts.EffectEvidence { Excavation = record.Evidence() };
                    if (!evidence.Excavation.Designated) throw new InvalidOperationException("Excavation designation was not observed.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Excavation write interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Excavation failed: " + error.GetType().Name) };
            }
        }
    }
}
