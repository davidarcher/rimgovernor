namespace RimGovernor.Brain.Policy;

/// <summary>
/// What one room improvement is worth to the people who sleep in the room (RG-HABITAT-001): the capability-days its
/// owners gain over the horizon sleeping at the Impressiveness stage it reaches rather than the one they sleep at now
/// (ForecastHousing.BedroomGain, whose mood is priced at the wellbeing weight), against what building it costs. A piece
/// that leaves the room's stage gains nothing (known zero). A fact the price needs that is unread leaves it unassessed
/// and it is not planned (rule 9).
/// </summary>
public abstract record RoomImprovementValue
{
    private RoomImprovementValue() { }

    /// <summary>The piece reaches another stage: its owners' gain over the horizon and its building cost, in capability-days.</summary>
    public sealed record Assessed(double Gain, double Cost) : RoomImprovementValue;

    /// <summary>The piece leaves the room at the stage its owners already sleep at: nothing is gained, whatever it costs.</summary>
    public sealed record NoGain : RoomImprovementValue;

    /// <summary>The price needs a fact left unread, and the piece is not planned: the piece's Beauty, MarketValue or WorkToBuild stat in its stuff, an owner's bedroom mood, the catalog's Impressiveness stages or a cost material's value; or the room is a common room, whose dining and recreation mood is not priced.</summary>
    public sealed record Unassessed : RoomImprovementValue;

    /// <summary>Whether the improvement is worth building: its gain exceeds its cost by more than the tie resolution.</summary>
    public bool Due => this is Assessed a && a.Gain - a.Cost > new ForecastValuePolicy().TieResolution;
}

/// <summary>A furniture piece's effect on the room it stands in, read from its definition in the stuff it is built of: its Beauty and MarketValue stats, its footprint and its work and cost list.</summary>
public sealed record RoomPieceEffect(double Beauty, double Wealth, int Cells, double Work, IReadOnlyList<Amount> Costs);

/// <summary>The colony's facts a room improvement is priced by.</summary>
public sealed record RoomImprovementFacts
{
    public required BedroomGainFacts Gains { get; init; }

    public required ImpressivenessLevels Levels { get; init; }

    /// <summary>The construction materials' values (ForecastCoupledContext.MaterialValues); null while unread.</summary>
    public IReadOnlyDictionary<Resource, ConstructionMaterialValue>? Values { get; init; }

    public double Horizon { get; init; } = ColonyModelPolicy.DefaultHorizonDays;
}

public static partial class RoomQualityRules
{
    /// <summary>
    /// The value of adding <paramref name="piece"/> (null when its room stats are unread) to <paramref name="room"/>,
    /// whose beds <paramref name="owners"/> own. The room's score after it is its observed score moved by the game's
    /// Impressiveness formula (RoomSpaceRules.Impressiveness) over the piece's wealth, its beauty spread over the room's
    /// cells and the space its footprint takes.
    /// </summary>
    public static RoomImprovementValue ImprovementValueOf(UpkeepRoom room, IReadOnlyList<PawnId> owners, RoomPieceEffect? piece, RoomImprovementFacts facts)
    {
        if (piece is null)
        {
            return new RoomImprovementValue.Unassessed();
        }
        if (owners.Count == 0)
        {
            return new RoomImprovementValue.Unassessed();
        }
        if (facts.Levels.Dull <= 0)
        {
            return new RoomImprovementValue.Unassessed();
        }
        var q = room.Quality;
        var cells = Math.Max(room.Cells, 1);
        var after = q.Impressiveness
            + RoomSpaceRules.Impressiveness(q.Space - (RoomSpaceRules.FurnishedSpaceLoss * piece.Cells), q.Beauty + (piece.Beauty / cells), q.Cleanliness, q.Wealth + piece.Wealth)
            - RoomSpaceRules.Impressiveness(q.Space, q.Beauty, q.Cleanliness, q.Wealth);
        var (now, then) = (facts.Levels.Stage(q.Impressiveness), facts.Levels.Stage(after));
        if (now == then)
        {
            return new RoomImprovementValue.NoGain();
        }
        var barracks = room.Role == RoomRole.Barracks;
        var gain = 0.0;
        foreach (var owner in owners)
        {
            if (facts.Gains.Gain(owner.Value, then, barracks) is not { } reached || facts.Gains.Gain(owner.Value, now, barracks) is not { } held)
            {
                return new RoomImprovementValue.Unassessed();
            }
            gain += reached - held;
        }
        if (facts.Values is not { } values)
        {
            return new RoomImprovementValue.Unassessed();
        }
        if (piece.Costs.Any(a => !values.ContainsKey(a.Resource)))
        {
            return new RoomImprovementValue.Unassessed();
        }
        return new RoomImprovementValue.Assessed(gain * facts.Horizon, LayoutClusterRules.PiecePrice((piece.Work, piece.Costs), values));
    }
}
