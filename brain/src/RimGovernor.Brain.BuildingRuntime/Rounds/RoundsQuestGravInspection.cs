namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Services a quest's grav inspection; the result carries wait ticks even when not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitGravInspection(ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var work = PolicyFunctions.SelectQuestGravInspection(read.Projection.Facts, read.Projection.Identity.Map);
        if (work.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (work.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(work.Quest, "", work.Reason, ""), call);
        }

        if (work.Waiting)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork }, false);
        }

        return (new RoundsPopulationJoinerResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_grav_inspection",
            "Grav inspection requires shared forecast worker, duration and outcome admission.") }, true);
    }
}