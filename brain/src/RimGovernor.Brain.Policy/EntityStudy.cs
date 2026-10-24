namespace RimGovernor.Brain.Policy;

// The study rule: a held entity is studied by the game's own WorkGiver_DarkStudyInteract, which offers a job
// only while CompStudiable.CurrentlyStudiable holds. The interval is the game's and the planner reads the
// result rather than restating it: a DarkStudy owner is owed while at least one held entity is currently
// studiable and not between studies. Nothing here forces a job, so the interval cannot be broken.
public static class EntityStudyRules
{
    /// <summary>
    /// The DarkStudy requirement the held entities owe the work planner: one owner while any held entity is
    /// currently studiable, none otherwise. Without Anomaly no entity is held and nothing is owed.
    /// </summary>
    public static EquatableArray<WorkRequirement> StudyWork(ContainmentPlanning p) =>
        p.Entities.Any(e => !e.Dead && e.Held && e.CurrentlyStudiable) ? [new WorkRequirement { Work = WorkType.DarkStudy }] : [];
}
