#nullable enable
using System;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // ResearchIntent (#941): the ordinary research slot's current project,
    // set through ResearchManager.SetCurrentProject. Native judges the
    // project against live research state when it applies; a project that
    // is already current applies again as it stands. Anomaly knowledge slots
    // are not selected here.
    internal sealed class ResearchActionHandler : IActionHandler
    {
        private static Common.Failure? Resolve(Operations.ResearchIntent? intent, out ResearchProjectDef project)
        {
            project = null!;
            if (intent == null || !intent.HasProjectDef || !ProtoBoundary.IsIdentifier(intent.ProjectDef))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Research selection requires a project.");
            var def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(intent.ProjectDef);
            if (def == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Unknown research project.");
            project = def;
            if (Find.ResearchManager.GetProject() == def) return null;
            if (def.knowledgeCategory != null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Anomaly knowledge projects are not selected through the ordinary slot.");
            if (def.IsFinished) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Project is already finished.");
            if (!def.CanStartNow) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native research validation refuses the project: prerequisites, bench or requirements unmet.");
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Research, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var failure = Resolve(action.Research, out var def);
            if (failure != null) throw new InvalidOperationException("Research prerequisites changed before apply: " + failure.Detail);
            var manager = Find.ResearchManager;
            var previous = manager.GetProject()?.defName ?? "";
            if (previous != def.defName) manager.SetCurrentProject(def);
            var current = manager.GetProject()?.defName ?? "";
            if (current != def.defName) throw new InvalidOperationException("Research selection did not verify after SetCurrentProject.");
            var effect = new Receipts.ResearchEffect { CurrentProjectDef = current };
            if (previous.Length != 0) effect.PreviousProjectDef = previous;
            return new Receipts.EffectEvidence { Research = effect };
        }
    }
}
