namespace RimGovernor.Brain.Policy;

/// <summary>
/// The step a building planner serves. The three housing values name the shelter family's housing planners
/// (#3257): the starter shelter with its bunks, bed ownership and bedrooms, and the spare room; their order is the
/// Needs' preconditions, not a latch. The comfort phases name which rooms Need a building planner serves
/// (rooms/comfort, rooms/hosting, rooms/party-spot), also ordered by Need preconditions. The empty phase is the
/// default.
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

    public bool Equals(Phase other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
