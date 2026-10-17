namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class JoinersRounds
{
    /// <summary>Holds a quest's grav inspection; a wait for the inspector is not handled.</summary>
    internal (Verdict Verdict, bool Handled) AdmitGravInspection(RoundsReading read, CancellationToken call)
    {
        var step = QuestGravInspection.SelectQuestGravInspection(read.Projection.Facts, read.Projection.Identity.Map);
        if (step.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (step.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(step.Quest, "", step.Reason, ""), call);
        }

        if (step.Waiting)
        {
            return (Verdict.BuildingReasonExistingWork, false);
        }

        return (Verdict.AwaitingPlan("unsupported_forecast_grav_inspection",
            "Grav inspection requires shared forecast worker, duration and outcome admission."), true);
    }
}
