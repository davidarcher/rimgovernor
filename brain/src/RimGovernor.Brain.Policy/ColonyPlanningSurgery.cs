namespace RimGovernor.Brain.Policy;

public static partial class ColonyPlanningRules
{
    /// <summary>
    /// The surgery ranking context over the projection: the work profiles and the health goals. The surgery review and the part-demand path read the same one.
    /// </summary>
    public static SurgeryContext SurgeryContext(ReviewFacts facts)
    {
        var ctx = new SurgeryContext { Goals = facts.GoalSurgeries.Items };
        var pawns = facts.ForecastObserved.WorkPawns.Pawns;
        {
            ctx = ctx with { Profiles = PawnRules.Profiles(pawns) };
        }
        return ctx;
    }
}
