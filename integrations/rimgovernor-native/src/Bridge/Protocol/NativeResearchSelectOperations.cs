using System;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // ResearchIntent: the project's research slot's current project, set
    // through ResearchManager.SetCurrentProject. An ordinary project fills the
    // ordinary slot; an anomaly knowledge project fills its category's
    // knowledge slot, which SetCurrentProject picks by knowledgeCategory.
    // Native judges the project against live research state when it applies
    // (CanStartNow also refuses a project the entity codex still hides); a
    // project that is already current in its slot applies again as it stands.
    internal sealed class ResearchActionHandler : IActionHandler
    {
        private static ResearchProjectDef? Current(ResearchProjectDef def) =>
            Find.ResearchManager.GetProject(def.knowledgeCategory);

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.ResearchIntent? intent, out ResearchProjectDef? project)
        {
            project = null;
            if (intent == null || intent.ProjectDef is null || !GameThread.IsIdentifier(intent.ProjectDef))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Research selection requires a project.");
            var def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail((intent.ProjectDef ?? ""));
            if (def == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Unknown research project.");
            project = def;
            if (def.knowledgeCategory != null && !ModsConfig.AnomalyActive) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Anomaly knowledge projects need the Anomaly expansion.");
            if (Current(def) == def) return null;
            if (def.IsFinished) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Project is already finished.");
            if (!def.CanStartNow) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Native research validation refuses the project: prerequisites, bench, codex or requirements unmet.");
            return null;
        }

        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(action.Intent?.Research, out _);

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(action.Intent?.Research, out var def);
            if (failure != null || def == null) throw new InvalidOperationException("Research prerequisites changed before apply: " + (failure?.Detail ?? ""));
            var manager = Find.ResearchManager;
            if (Current(def)?.defName != def.defName) manager.SetCurrentProject(def);
            if (Current(def)?.defName != def.defName) throw new InvalidOperationException("Research selection did not verify after SetCurrentProject.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.ResearchCase() };
        }
    }
}
