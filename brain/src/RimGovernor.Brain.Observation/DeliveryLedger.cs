namespace RimGovernor.Brain.Observation;

/// <summary>The production kind a delivery was counted under. The source-to-channel mapping is the consumer's, from its own census.</summary>
public enum DeliveryKind
{
    [WireName("")]
    None = 0,
    Crop,
    Fish,
    Forage,
    [WireName("animal_product")]
    AnimalProduct,
}

/// <summary>
/// One counter: a growing zone id (crop), a water body's root cell "x,z" (fish), a plant def (forage) or an
/// animal race (animal product), with the delivered def.
/// </summary>
public readonly record struct DeliveryKey(DeliveryKind Kind, string SourceID, string Def);

/// <summary>A cumulative counter within one <see cref="DeliveryLedger"/> load token.</summary>
public readonly record struct DeliveryCount(long Units, long LastTick, double Nutrition);

/// <summary>A player pawn's kill of an animal, keyed by its corpse's thing id (the food census's stock id) and carrying the animal's own pawn id. PotentialNutrition is the meat it would yield.</summary>
public sealed record Kill
{
    public string CorpseID { get; init; } = "";

    public string PawnID { get; init; } = "";

    public string Race { get; init; } = "";

    public double BodySize { get; init; }

    public double PotentialNutrition { get; init; }

    public long Tick { get; init; }
}

/// <summary>One butcher recipe's products from a corpse, counted where they are made; a reader links it to its <see cref="Kill"/> by CorpseID.</summary>
public sealed record Butcher
{
    public string CorpseID { get; init; } = "";

    public string Recipe { get; init; } = "";

    public string MeatDef { get; init; } = "";

    public long MeatUnits { get; init; }

    public long LeatherUnits { get; init; }

    public double MeatNutrition { get; init; }

    public long Tick { get; init; }
}

/// <summary>
/// Native's cumulative production-site delivery counters. A reader diffs successive ledgers of one load token
/// and re-baselines when the token changes (a load or restart). Kills and Butchers are every record of the
/// load token in tick order.
/// </summary>
public sealed record DeliveryLedger
{
    public string LoadToken { get; init; } = "";

    public IReadOnlyDictionary<DeliveryKey, DeliveryCount> Counts { get; init; } = new Dictionary<DeliveryKey, DeliveryCount>();

    public IReadOnlyList<Kill> Kills { get; init; } = [];

    public IReadOnlyList<Butcher> Butchers { get; init; } = [];
}

public static partial class ObservationPackage
{
    private static DeliveryKind DeliveryKindOf(O.DeliverySourceKind kind) => kind switch
    {
        O.DeliverySourceKind.Crop => DeliveryKind.Crop,
        O.DeliverySourceKind.Fish => DeliveryKind.Fish,
        O.DeliverySourceKind.Forage => DeliveryKind.Forage,
        O.DeliverySourceKind.AnimalProduct => DeliveryKind.AnimalProduct,
        _ => DeliveryKind.None,
    };

    /// <summary>Unknown for a missing, unavailable or malformed census, never an empty ledger: zero deliveries would read as a measured zero.</summary>
    internal static Fact<DeliveryLedger> ColonyDeliveryLedger(O.DeliveryLedgerSection? section, long tick)
    {
        var f = section?.Observed;
        if (f is null || !ValidDeliveryLedger(f, tick))
        {
            return Unknown<DeliveryLedger>();
        }
        var counts = new Dictionary<DeliveryKey, DeliveryCount>(f.Rows.Count);
        var kills = new List<Kill>(f.Kills.Count);
        foreach (var k in f.Kills)
        {
            kills.Add(new Kill { CorpseID = k.CorpseId, PawnID = k.PawnId, Race = k.Race, BodySize = k.BodySize, PotentialNutrition = k.PotentialNutrition, Tick = k.Tick });
        }
        var butchers = new List<Butcher>(f.Butchers.Count);
        foreach (var b in f.Butchers)
        {
            butchers.Add(new Butcher { CorpseID = b.CorpseId, Recipe = b.Recipe, MeatDef = b.MeatDef, MeatUnits = b.MeatUnits, MeatNutrition = b.MeatNutrition, LeatherUnits = b.LeatherUnits, Tick = b.Tick });
        }
        foreach (var row in f.Rows)
        {
            counts[new DeliveryKey(DeliveryKindOf(row.SourceKind), row.SourceId, row.DefName)] = new DeliveryCount(row.Units, row.LastTick, row.Nutrition);
        }
        return Known(new DeliveryLedger { LoadToken = f.Epoch, Counts = counts, Kills = kills, Butchers = butchers });
    }
}
