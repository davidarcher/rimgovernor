namespace RimGovernor.Brain.Observation;

public sealed partial record ColonyProjection
{
    /// <summary>
    /// The surgery ranking context over the projection: the hospital bed, the work
    /// profiles and the health goals. The surgery review and the part-demand path read the same one.
    /// </summary>
    public SurgeryContext SurgeryContext()
    {
        var ctx = new SurgeryContext { Goals = Facts.GoalSurgeries.Items, HospitalBed = SurgeryRules.HospitalBedReady(Facts.Sleeping) };
        if (WorkPawns is WorkPawnsRead.Read { Pawns: var pawns })
        {
            ctx = ctx with { Profiles = PawnRules.Profiles(pawns) };
        }
        return ctx;
    }
}
