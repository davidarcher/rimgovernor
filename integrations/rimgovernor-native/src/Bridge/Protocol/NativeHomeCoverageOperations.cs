#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Progress record for one admitted ExtendHome: the facility target, the
    /// shape token it was admitted against and the Home revision after the
    /// write. Home is a synchronous area write, so the effect is known at
    /// execute time; a later observation re-derives the footprint and checks
    /// every cell is still Home (a player removal since the extension is an
    /// unsuccessful outcome, not a pending one).
    /// </summary>
    internal sealed class NativeHomeCoverageRecord
    {
        internal readonly Map Map;
        internal readonly string Target;
        internal readonly string Shape;
        internal readonly int Changed;
        internal long Revision;
        internal NativeHomeCoverageRecord(Map map, string target, string shape, int changed, long revision)
        { Map = map; Target = target; Shape = shape; Changed = changed; Revision = revision; }

        internal Receipts.HomeEffect Evidence(List<IntVec3>? cells)
        {
            var state = HomeCoverage.State(Map);
            var effect = new Receipts.HomeEffect { ShapeToken = Shape, Revision = state.Revision, ChangedCells = Changed,
                Snapshot = new Receipts.SnapshotEvidence { EntityId = Target, BeforeToken = Shape } };
            if (cells != null) { effect.Snapshot.AfterToken = HomeCoverage.Shape(Target, cells); effect.Covered = cells.All(c => Map.areaManager.Home[c]); }
            return effect;
        }

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            if (Map != ProtoBoundary.ResolveMap(context))
            {
                result.CompleteInspection = false;
                result.Unknown = new Receipts.UnknownEffect { Reason = "Extended facility's map is unavailable." };
                return result;
            }
            var cells = HomeCoverage.Scope(Map, Target);
            var evidence = new Receipts.EffectEvidence { Home = Evidence(cells) };
            if (cells == null)
                result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = "Bounded visible native facility geometry is no longer available." };
            else if (evidence.Home.Covered)
                result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else
                result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = "Home was removed over the facility footprint since the extension." };
            return result;
        }
    }

    /// <summary>
    /// Typed ExtendHome: adds Home over the missing cells of one exact
    /// facility's bounded footprint (HomeCoverage.Scope: the building plus
    /// adjacent enclosed roofed rooms, or a stockpile's cells) under the
    /// footprint's shape hash and the map-wide Home revision the controller
    /// read from the upkeep census. Both are recomputed at apply time (#244);
    /// no per-target CAS token exists, so the shape and revision rules are the
    /// whole check. Never removes Home, paints terrain or touches allowed areas.
    /// </summary>
    internal static class NativeHomeCoverageOperations
    {
        internal const string Kind = "Home extension";

        internal static bool Valid(Operations.ExtendHome? command) => command != null
            && NativeDraftProtocol.ValidEntityTokenOptional(command.Target)
            && command.HasShapeToken && ProtoBoundary.IsIdentifier(command.ShapeToken)
            && command.HasRevision && command.Revision >= 0;

        private static bool Prepare(Operations.ExtendHome command, Common.ObservationContext context,
            out Map? map, out List<IntVec3>? cells, out List<IntVec3>? missing, out Common.Failure failure)
        {
            map = null; cells = null; missing = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Home extension requires an exact facility identity (a colonist building's unique load id or stockpile:<id>), its footprint shape token and the Home revision it was read under.");
            if (!Valid(command)) return false;
            var loaded = ProtoBoundary.ResolveMap(context);
            if (loaded == null) return false;
            List<IntVec3>? scope = null;
            // The apply-time precondition list for Home extension
            // (action-contracts.md), one rule at a time.
            var rules = new ApplyPreconditions(Kind)
                .Present(() => (scope = HomeCoverage.Scope(loaded, command.Target.EntityId)) != null, "bounded visible native facility geometry is unavailable")
                .Token(() => HomeCoverage.Shape(command.Target.EntityId, scope!) == command.ShapeToken, "the facility footprint changed since it was read")
                .Token(() => HomeCoverage.State(loaded).Revision == command.Revision, "the Home area changed since it was read");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            map = loaded; cells = scope; missing = scope!.Where(c => !loaded.areaManager.Home[c]).ToList();
            return true;
        }

        private static Receipts.HomeEffect Projected(Operations.ExtendHome command, List<IntVec3>? missing) => new Receipts.HomeEffect
        {
            Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Target.EntityId, BeforeToken = command.ShapeToken },
            ShapeToken = command.ShapeToken, Revision = command.Revision, ChangedCells = missing?.Count ?? 0, Covered = missing != null && missing.Count == 0,
        };

        // A footprint or revision that moved since the read is an evaluated
        // refusal (accepted false with the rule that failed), not a failure:
        // the controller re-reads the census and proposes again.
        internal static Operations.PreviewReply Preview(Operations.ExtendHome command, Common.ObservationContext context)
        {
            if (!Valid(command)) return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Home extension preview requires an exact facility identity, its footprint shape token and a non-negative Home revision.") };
            if (ProtoBoundary.ResolveMap(context) == null) return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Loaded map required.") };
            var accepted = Prepare(command, context, out _, out _, out var missing, out var failure);
            var evaluation = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = accepted,
                Projected = new Receipts.EffectEvidence { Home = Projected(command, missing) } };
            if (!accepted) evaluation.Reason = failure.Detail;
            return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = evaluation });
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.ExtendHome;
            try
            {
                if (!Prepare(command, context, out var map, out var cells, out var missing, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply; handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(command, context, out map, out cells, out missing, out failure))
                        throw new InvalidOperationException("Home scope changed before the extension.");
                    var record = new NativeHomeCoverageRecord(map!, command.Target.EntityId, command.ShapeToken, missing!.Count, command.Revision);
                    state.HomeCoverage.Add(pre.Attempt.Clone(), record);
                    // Area_Home.Set advances the persisted revision once per
                    // changed cell (HomeCoverageTool.cs); the receipt carries
                    // the revision after the last cell.
                    foreach (var c in missing) map!.areaManager.Home[c] = true;
                    record.Revision = HomeCoverage.State(map!).Revision;
                    evidence = new Receipts.EffectEvidence { Home = record.Evidence(cells) };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Home extension admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Home extension requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
