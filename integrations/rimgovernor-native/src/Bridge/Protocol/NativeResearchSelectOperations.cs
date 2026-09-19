#nullable enable
using System;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Tracks an admitted SelectResearch attempt so a later ObserveProgress poll
    // re-verifies it from live research state alone, like every Native*Record.
    internal sealed class NativeResearchSelectRecord
    {
        internal readonly ResearchProjectDef Project;
        internal readonly string Previous;
        internal readonly string BeforeToken;
        internal NativeResearchSelectRecord(ResearchProjectDef project, string previous, string beforeToken)
        { Project = project; Previous = previous; BeforeToken = beforeToken; }
    }

    // Typed SelectResearch: the ordinary research slot's current project, set
    // through ResearchManager.SetCurrentProject under the same CAS the read
    // exposes (the observed research snapshot token) and the shared
    // attempt/lease/ledger admission every rimgovernor/operations_execute
    // command requires. The outcome is achieved while the project stays
    // current or once it finishes; anomaly knowledge slots are not selected here.
    // An execute dispatched under a running clock omits the token (#244): the
    // token hashes every project's progress, which moves every tick while a
    // project is current, and Refusal is the check that refuses a project the
    // world no longer admits. A preview still sends it.
    internal static class NativeResearchSelectOperations
    {
        private static bool Prepare(Operations.SelectResearch? command, Common.ObservationContext context, Map? map,
            out ResearchProjectDef project, out string token, out Common.Failure failure)
        {
            project = null!; token = "";
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Research selection requires a known project and, when sent, a non-blank research snapshot token.");
            if (command == null || map == null || !command.HasProjectDef || (command.HasExpectedSnapshotToken && command.ExpectedSnapshotToken.Length == 0)) return false;
            var def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(command.ProjectDef);
            if (def == null) return false;
            token = NativeResearchObservationTools.CurrentToken(context, map);
            if (command.HasExpectedSnapshotToken && token != command.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Research state changed since it was read; inspect again."); return false; }
            project = def;
            return true;
        }

        private static string Refusal(ResearchProjectDef def)
        {
            if (def.knowledgeCategory != null) return "Anomaly knowledge projects are not selected through the ordinary slot.";
            if (def.IsFinished) return "Project is already finished.";
            if (!def.CanStartNow) return "Native research validation refuses the project: prerequisites, bench or requirements unmet.";
            return "";
        }

        internal static Operations.PreviewReply Preview(Operations.SelectResearch command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, ProtoBoundary.LoadedMap(context), out var def, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                var reason = Refusal(def);
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = reason.Length == 0, Reason = reason } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Research selection preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.SelectResearch;
            try
            {
                var map = ProtoBoundary.LoadedMap(context);
                if (!Prepare(command, context, map, out var def, out var before, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                var reason = Refusal(def);
                if (reason.Length != 0) return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, reason) };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                var manager = Find.ResearchManager;
                var previous = manager.GetProject()?.defName ?? "";
                state.ResearchSelections.Add(pre.Attempt.Clone(), new NativeResearchSelectRecord(def, previous, before));
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success || NativeResearchObservationTools.CurrentToken(context, map) != before || Refusal(def).Length != 0)
                        throw new InvalidOperationException("Research admission changed before effect.");
                    manager.SetCurrentProject(def);
                    var current = manager.GetProject()?.defName ?? "";
                    evidence = Evidence(previous, before, NativeResearchObservationTools.CurrentToken(context, map), current);
                    if (current != def.defName) throw new InvalidOperationException("Research selection did not verify after SetCurrentProject.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Research selection admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted research selection requires observation: " + error.GetType().Name) };
            }
        }

        private static Receipts.EffectEvidence Evidence(string previous, string before, string after, string current)
        {
            var effect = new Receipts.ResearchEffect { CurrentProjectDef = current,
                Snapshot = new Receipts.SnapshotEvidence { EntityId = "research-manager", BeforeToken = before, AfterToken = after } };
            if (previous.Length != 0) effect.PreviousProjectDef = previous;
            return new Receipts.EffectEvidence { Research = effect };
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeResearchSelectRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact research state is unavailable." } };
            try
            {
                var map = ProtoBoundary.LoadedMap(context);
                var current = Find.ResearchManager.GetProject()?.defName ?? "";
                // A finished project leaves the slot empty; the selection still
                // achieved its outcome, so the evidence names the project.
                var achieved = current == record.Project.defName || record.Project.IsFinished;
                var evidence = Evidence(record.Previous, record.BeforeToken,
                    NativeResearchObservationTools.CurrentToken(context, map), achieved ? record.Project.defName : current);
                result.CompleteInspection = true;
                if (achieved) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = "The admitted project is no longer current and is not finished." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
