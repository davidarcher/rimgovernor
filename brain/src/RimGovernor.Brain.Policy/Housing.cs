namespace RimGovernor.Brain.Policy;

/// <summary>
/// The step a phased goal (MaintainHousing, EnsureComfort, MaintainMedicalReserves) leaves owed, latched per goal
/// in RoundsLatches. MaintainHousing walks its phases in order and each has its own planner: the starter shelter
/// with its bunks, then bed ownership and bedrooms, then the spare room. The empty phase is the default.
/// </summary>
public readonly record struct Phase(string Value)
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>The empty phase: no step owed.</summary>
    public static Phase None => default;

    /// <summary>The foothold gates (a sleeping place and an indoor place for everyone) are not both met.</summary>
    public static Phase HousingShelter => new("shelter");

    /// <summary>ReviewSleeping has a colonist without a suitable owned bed, an unused one or a bedroom step due.</summary>
    public static Phase HousingSleeping => new("sleeping");

    /// <summary>Indoor capacity keeps no spare place beyond the population. Raised from StageReserves.</summary>
    public static Phase HousingExpansion => new("expansion");

    /// <summary>
    /// EnsureComfort's basic step: the basic facilities (a reachable table, seat and recreation source, foothold
    /// priority) before the ranked project. Each phase has its own planner.
    /// </summary>
    public static Phase ComfortBasic => new("basic");

    /// <summary>EnsureComfort's ranked step: hosting rooms and proof of use, from StageDevelopment.</summary>
    public static Phase ComfortRanked => new("ranked");

    /// <summary>The PartySpot step, owed once neither facility phase is: the ranked planner serves it (RoundsFacts.PartySpotOwed).</summary>
    public static Phase ComfortSpot => new("spot");

    /// <summary>MaintainMedicalReserves' step: care for the sick (rest and a hospital bed) before the medicine stock.</summary>
    public static Phase MedicalCare => new("care");

    /// <summary>MaintainMedicalReserves' stock step.</summary>
    public static Phase MedicalReserves => new("reserves");

    public bool Equals(Phase other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>MaintainHousing's goal and assessment for one review.</summary>
public sealed record HousingReview
{
    public Phase Phase { get; init; }
    public int Priority { get; init; }
    public Fact<double> Deficit { get; init; }
    public Fact<bool> Recovered { get; init; }
}

public static class RoundsLatchesPhaseExtensions
{
    /// <summary>The goal's latched phase; empty for an unphased goal or once the goal recovered.</summary>
    public static Phase Phase(this RoundsLatches l, ConcernId goal)
    {
        if (goal == PolicyFunctions.MaintainHousing)
        {
            return l.Housing;
        }
        if (goal == PolicyFunctions.EnsureComfort)
        {
            return l.Comfort;
        }
        if (goal == PolicyFunctions.MaintainMedicalReserves)
        {
            return l.Medical;
        }
        return new Phase("");
    }
}

public static partial class PolicyFunctions
{

    /// <summary>
    /// MaintainHousing, the one housing goal: the starter shelter at foothold priority first, then sleeping
    /// upkeep, then expansion. A later phase never opens while an earlier one is owed, so the first shelter and
    /// its beds come before any bedroom or spare room. A sleeping census that is unknown and not latched active
    /// does not hold expansion.
    /// </summary>
    internal static HousingReview ReviewHousing(RoundsFacts f, RoundsLatches previous, RoundsPolicy p, bool sleepingActive)
    {
        var shelter = AllFacts(FootholdShelter(f), FootholdSleeping(f));
        // The upkeep census counts any issued housing plan; only one the sleeping phase issued holds that phase open.
        var issued = f.IsIssued(MaintainHousing) && previous.Housing == Phase.HousingSleeping;
        var sleeping = f.SleepingRecovered;
        if (issued)
        {
            sleeping = Known(false);
        }
        var sleepingKnown = sleeping.IsKnown;
        var sleepingPriority = 3;
        if (!sleepingKnown && !sleepingActive)
        {
            sleepingPriority = 4;
        }
        var expansion = Unknown<bool>();
        if (f.Colonists.TryGetValue(out var n) && n > 0)
        {
            expansion = Measured(f.IndoorCapacity, capacity => capacity >= n + HousingSparePlaces);
        }
        var staged = p.ColonyStage >= ColonyStage.Reserves;
        var recovered = AllFacts(shelter, sleeping);
        if (staged)
        {
            recovered = AllFacts(recovered, expansion);
        }
        if (!Positive(shelter))
        {
            return new HousingReview
            {
                Phase = Phase.HousingShelter,
                Priority = 2,
                Deficit = Unknown<double>(),
                Recovered = shelter,
            };
        }
        if (!Positive(sleeping) && (sleepingKnown || sleepingActive))
        {
            return SleepingHousing(sleeping, sleepingPriority);
        }
        if (staged && !Positive(expansion))
        {
            return new HousingReview
            {
                Phase = Phase.HousingExpansion,
                Priority = 4,
                Deficit = RoundsDeficit(MaintainHousing, f, p),
                Recovered = expansion,
            };
        }
        if (!Positive(sleeping))
        {
            return SleepingHousing(sleeping, sleepingPriority);
        }
        return new HousingReview { Priority = 2, Recovered = recovered };
    }

    /// <summary>The bedroom phase: a confirmed deficit ranks at Known(1.0); an unknown census stays DevelopmentUnknown.</summary>
    private static HousingReview SleepingHousing(Fact<bool> sleeping, int priority)
    {
        var deficit = Unknown<double>();
        if (sleeping.IsKnown)
        {
            deficit = Known(1.0);
        }
        return new HousingReview
        {
            Phase = Phase.HousingSleeping,
            Priority = priority,
            Deficit = deficit,
            Recovered = sleeping,
        };
    }
}
