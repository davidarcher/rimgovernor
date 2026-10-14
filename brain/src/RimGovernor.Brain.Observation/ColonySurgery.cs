namespace RimGovernor.Brain.Observation;

public sealed partial record ColonyProjection
{
    /// <summary>
    /// The surgery ranking context over the projection: the hospital bed and
    /// the work profiles. The surgery review and the part-demand path read the same one.
    /// </summary>
    public SurgeryContext SurgeryContext()
    {
        var ctx = new SurgeryContext();
        if (PolicyFunctions.HospitalBedReady(Facts.Sleeping).TryGetValue(out var bed) && bed)
        {
            ctx = ctx with { HospitalBed = true };
        }
        if (WorkPawns.TryGetValue(out var pawns))
        {
            ctx = ctx with { Profiles = PolicyFunctions.Profiles(pawns) };
        }
        return ctx;
    }
}
