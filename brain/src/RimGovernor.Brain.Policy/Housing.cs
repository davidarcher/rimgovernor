namespace RimGovernor.Brain.Policy;

/// <summary>
/// The step a phased goal (MaintainHousing, MaintainMedicalReserves) leaves owed, latched per goal in RoundsLatches.
/// MaintainHousing walks its phases in order and each has its own planner: the starter shelter with its bunks, then
/// bed ownership and bedrooms, then the spare room. The comfort phases are not latched: they name which rooms Need
/// a building planner serves (rooms/comfort, rooms/hosting, rooms/party-spot), ordered by Need preconditions. The
/// empty phase is the default.
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

    /// <summary>Indoor capacity keeps no spare place beyond the population.</summary>
    public static Phase HousingExpansion => new("expansion");

    /// <summary>
    /// The rooms/comfort Need's planner: the basic facilities (a reachable table, seat and recreation source) before
    /// the hosting rooms.
    /// </summary>
    public static Phase ComfortBasic => new("basic");

    /// <summary>The rooms/hosting Need's planner: hosting rooms and proof of use.</summary>
    public static Phase ComfortRanked => new("ranked");

    /// <summary>The rooms/party-spot Need's planner: one PartySpot in the best shared room.</summary>
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
        var expansion = Unknown<bool>();
        if (f.Colonists.TryGetValue(out var n) && n > 0)
        {
            expansion = Measured(f.IndoorCapacity, capacity => capacity >= n + HousingSparePlaces);
        }
        var recovered = AllFacts(shelter, sleeping, expansion);
        if (!Positive(shelter))
        {
            return new HousingReview
            {
                Phase = Phase.HousingShelter,
                Deficit = Unknown<double>(),
                Recovered = shelter,
            };
        }
        if (!Positive(sleeping) && (sleepingKnown || sleepingActive))
        {
            return SleepingHousing(sleeping);
        }
        if (!Positive(expansion))
        {
            return new HousingReview
            {
                Phase = Phase.HousingExpansion,
                Deficit = RoundsDeficit(MaintainHousing, f, p),
                Recovered = expansion,
            };
        }
        if (!Positive(sleeping))
        {
            return SleepingHousing(sleeping);
        }
        return new HousingReview { Recovered = recovered };
    }

    /// <summary>The bedroom phase: a confirmed deficit ranks at Known(1.0); an unknown census stays DevelopmentUnknown.</summary>
    private static HousingReview SleepingHousing(Fact<bool> sleeping)
    {
        var deficit = Unknown<double>();
        if (sleeping.IsKnown)
        {
            deficit = Known(1.0);
        }
        return new HousingReview
        {
            Phase = Phase.HousingSleeping,
            Deficit = deficit,
            Recovered = sleeping,
        };
    }
}
