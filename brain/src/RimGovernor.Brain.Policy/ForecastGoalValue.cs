namespace RimGovernor.Brain.Policy;

/// <summary>The capability-days a whole goal is worth at each importance level (#3503).</summary>
public sealed record GoalImportanceValues(double Low, double Normal, double High)
{
    public double Of(Importance importance) => importance switch
    {
        Importance.Low => Low,
        Importance.Normal => Normal,
        _ => High,
    };
}

/// <summary>
/// A colonist a worn-count goal wants dressed in its garment (def, and stuff when the goal names one, else ""), and the fraction of
/// the goal's progress that colonist wearing it is (#3816): paid when a selected commitment's outfit change dresses the pawn in it.
/// </summary>
public sealed record ForecastGoalWearer(string Pawn, string Garment, string Stuff, double Share);

/// <summary>A research project a goal waits on, and the fraction of the goal's progress one research point is.</summary>
public sealed record ForecastGoalResearch(string Project, double SharePerPoint);

/// <summary>Capability-days one unit of progress on a project (<see cref="ForecastProjectProgress.Project"/>) adds to a goal, as tier 2 prices it (#3505).</summary>
public sealed record ForecastGoalUnit(string Project, double Value);

/// <summary>
/// One unmet goal as the comparator values it (#3503): its need id, its effective importance and deadline day, and the
/// two ways a schedule can progress it. Progress is the fraction of the whole goal; the ledger's goal term is the goal's
/// worth (<see cref="ForecastValuePolicy.GoalWorth"/>) times the progress the outcome makes inside the horizon.
/// </summary>
public sealed record ForecastGoalValue(string Goal, Importance Importance, int? DeadlineDay)
{
    /// <summary>The colonists a worn-count goal still wants wearing its garment.</summary>
    public EquatableArray<ForecastGoalWearer> Wearers { get; init; }

    /// <summary>Research projects the goal is waiting on; an hour of work at one of them is progress.</summary>
    public EquatableArray<ForecastGoalResearch> Research { get; init; }

    /// <summary>
    /// The marginal value of the goal's project progress from the tier-2 model (#3505): the goal's value at its modelled completion day
    /// moved earlier by what a unit advances it. A goal tier 2 priced is paid by these and not by <see cref="Research"/>; one it did not keeps the flat value.
    /// </summary>
    public EquatableArray<ForecastGoalUnit> Units { get; init; }

    /// <summary>True when tier 2 priced the goal: <see cref="Units"/> replace <see cref="Research"/>, even when every unit is worth nothing.</summary>
    public bool Priced { get; init; }
}

/// <summary>One goal's contribution to an outcome's ledger (#3503): the fraction of the goal the outcome progresses, the goal's worth after its deadline's decay, and their product in capability-days gained.</summary>
public sealed record ForecastGoalContribution(string Goal, double Progress, double Worth, double Value);
