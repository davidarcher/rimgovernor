namespace RimGovernor.Brain.Policy;

// The study rule: a held entity is studied by the game's own WorkGiver_DarkStudyInteract, which offers a job
// only while CompStudiable.CurrentlyStudiable holds. The interval is the game's and the planner reads the
// result rather than restating it: a DarkStudy owner is owed while at least one held entity is currently
// studiable and not between studies. Nothing here forces a job, so the interval cannot be broken.
public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>WorkDarkStudy</c>: the Anomaly WorkTypeDef the study work giver issues its jobs through (workType
    /// DarkStudy, relevant skill Intellectual, from the work type row native reports).
    /// </summary>
    public static readonly WorkType WorkDarkStudy = new("DarkStudy");

    /// <summary>
    /// Go <c>StudyWork</c>: the DarkStudy requirement the held entities owe the work planner: one owner while
    /// any held entity is currently studiable, none otherwise. A held entity whose held or currently-studiable
    /// fact is unread makes the whole answer unknown, so the planner waits instead of guessing; Reason says
    /// which entity and fact. Without Anomaly (entities unknown) no entity is held and nothing is owed.
    /// </summary>
    public static (Fact<IReadOnlyList<WorkRequirement>> Requirements, string Reason) StudyWork(ContainmentPlanning p)
    {
        var none = Known<IReadOnlyList<WorkRequirement>>([]);
        if (!p.Entities.TryGetValue(out var entities))
        {
            return (none, "");
        }
        var owed = false;
        foreach (var e in entities)
        {
            if (e.Dead.TryGetValue(out var dead) && dead)
            {
                continue;
            }
            if (!e.Held.TryGetValue(out var held))
            {
                return (Unknown<IReadOnlyList<WorkRequirement>>(), $"whether a platform holds entity {e.Pawn} is unread");
            }
            if (!held)
            {
                continue;
            }
            if (!e.CurrentlyStudiable.TryGetValue(out var studiable))
            {
                return (Unknown<IReadOnlyList<WorkRequirement>>(), $"whether held entity {e.Pawn} is currently studiable is unread");
            }
            owed = owed || studiable;
        }
        if (!owed)
        {
            return (none, "");
        }
        return (Known<IReadOnlyList<WorkRequirement>>([new WorkRequirement { Work = WorkDarkStudy }]), "");
    }
}
