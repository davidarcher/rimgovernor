#nullable enable
using System;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // ResearchIntent (#941): the project's research slot's current project, set
    // through ResearchManager.SetCurrentProject. An ordinary project fills the
    // ordinary slot; an anomaly knowledge project (#1745) fills its category's
    // knowledge slot, which SetCurrentProject picks by knowledgeCategory.
    // Native judges the project against live research state when it applies
    // (CanStartNow also refuses a project the entity codex still hides); a
    // project that is already current in its slot applies again as it stands.
    internal sealed class ResearchActionHandler : IActionHandler
    {
        private static ResearchProjectDef? Current(ResearchProjectDef def) =>
            Find.ResearchManager.GetProject(def.knowledgeCategory);

        private static Common.Failure? Resolve(Operations.ResearchIntent? intent, out ResearchProjectDef project)
        {
            project = null!;
            if (intent == null || !intent.HasProjectDef || !ProtoBoundary.IsIdentifier(intent.ProjectDef))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Research selection requires a project.");
            var def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(intent.ProjectDef);
            if (def == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Unknown research project.");
            project = def;
            if (def.knowledgeCategory != null && !ModsConfig.AnomalyActive) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Anomaly knowledge projects need the Anomaly expansion.");
            if (Current(def) == def) return null;
            if (def.IsFinished) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Project is already finished.");
            if (!def.CanStartNow) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native research validation refuses the project: prerequisites, bench, codex or requirements unmet.");
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Research, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var failure = Resolve(action.Research, out var def);
            if (failure != null) throw new InvalidOperationException("Research prerequisites changed before apply: " + failure.Detail);
            var manager = Find.ResearchManager;
            var previous = Current(def)?.defName ?? "";
            if (previous != def.defName) manager.SetCurrentProject(def);
            var current = Current(def)?.defName ?? "";
            if (current != def.defName) throw new InvalidOperationException("Research selection did not verify after SetCurrentProject.");
            var effect = new Receipts.ResearchEffect { CurrentProjectDef = current };
            if (previous.Length != 0) effect.PreviousProjectDef = previous;
            return new Receipts.EffectEvidence { Research = effect };
        }
    }
}
