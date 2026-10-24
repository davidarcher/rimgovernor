namespace RimGovernor.Brain.Policy;

/// <summary>
/// The step a building planner serves. The two housing values name the shelter family's housing steps (#3257,
/// #3659): the day-one stand-in spots, and bed ownership and bedrooms; their order is the sleeping planner's own
/// order, not a latch. The comfort phases name which rooms Need a building planner serves
/// (rooms/comfort, rooms/hosting, rooms/party-spot, rooms/work-chairs), also ordered by Need preconditions. The empty phase is the
/// default.
/// </summary>
public readonly record struct Phase(string Value)
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>The empty phase: no step owed.</summary>
    public static Phase None => default;

    /// <summary>
    /// The day-one floor (#3659): the foothold gates (a sleeping place and an indoor place for everyone) are not both
    /// met, and the sleeping planner places stand-in sleeping spots (ShelterRules.StandInsOwed), Survive tier.
    /// </summary>
    public static Phase HousingShelter => new("shelter");

    /// <summary>ReviewSleeping has a colonist without a suitable owned bed, an unused one or a bedroom step due.</summary>
    public static Phase HousingSleeping => new("sleeping");

    /// <summary>
    /// The rooms/comfort Need's planner: the basic facilities (a reachable table, seat and recreation source) before
    /// the hosting rooms.
    /// </summary>
    public static Phase ComfortBasic => new("basic");

    /// <summary>The rooms/hosting Need's planner: hosting rooms and proof of use.</summary>
    public static Phase ComfortRanked => new("ranked");

    /// <summary>The rooms/party-spot Need's planner: one PartySpot in the best shared room.</summary>
    public static Phase ComfortSpot => new("spot");

    /// <summary>The rooms/work-chairs Need's planner: the chosen work chair on a standing bench's interaction cell.</summary>
    public static Phase ComfortWorkChair => new("work_chair");

    public bool Equals(Phase other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
