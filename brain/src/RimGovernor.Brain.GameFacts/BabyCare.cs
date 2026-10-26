using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>Native childcare opportunities, never an allocation of carers or a prediction of random destinations.</summary>
public sealed record BabyCareRead
{
    public required BabyCareActiveRead? Active { get; init; }
    public required BabyCareFoodRead? Food { get; init; }
    public required BabyCarePlayRead? Play { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyCareOpportunityRead>))]
    public required EquatableArray<BabyCareOpportunityRead> Opportunities { get; init; }
}

public enum BabyCareKind { Breastfeed, BottleFeed, StaticPlay, WalkingPlay, ToyPlay, Carry }
public sealed record BabyCarePlayRead(double Level);
public sealed record BabyCareFoodRead(double Level, double FallPerTick, double GainPerTick)
{
    /// <summary>Native hunger predicate for automatic feeding; opportunity and job start are not intake.</summary>
    public bool WantsFeeding { get; init; }
}
/// <summary>The current native job and raw Position cells used by clamor and social interaction. Held position is deliberately different.</summary>
public sealed record BabyCareActiveRead(string Carer, BabyCareKind Kind, Cell BabyPosition, Cell CarerPosition,
    ServicePositionRead FuturePositions, ActivitySocialMode Social);
/// <summary>One eligible native service. FoodAvailable is normalized nutrition (charge or the usable food stack),
/// not a colony stock promise. Positions enclose all native choices; no probability over them is implied.</summary>
public sealed record BabyCareOpportunityRead(string Carer, BabyCareKind Kind, double FoodAvailable,
    ServicePositionRead BabyPosition, ServicePositionRead CarerPosition, ActivitySocialMode Social)
{
    /// <summary>Native milk activation or usable bottle food now, distinct from future refill opportunities.</summary>
    public bool ReadyToFeed { get; init; }
    /// <summary>Native lactation charge refill, normalized to the baby's food capacity; zero for a finite food stack.</summary>
    public double FoodRefillPerTick { get; init; }
}
