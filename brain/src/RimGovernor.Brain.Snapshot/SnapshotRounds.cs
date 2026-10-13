using StoreRounds = RimGovernor.Brain.Store.Rounds;
using StoreRoundsResult = RimGovernor.Brain.Store.RoundsResult;

namespace RimGovernor.Brain.Snapshot;

/// <summary>
/// One enabled rounds as recorded: the input the review passed policy DetectRounds and, optionally, the journal's
/// review cursor it filed. Facts carry every census the planners of that tick read (Upkeep, Research, ...).
/// </summary>
public sealed class SnapshotRounds
{
    /// <summary>Provenance: the world and tick, and whatever the recorder adds (a case name, a commit).</summary>
    public string Recorded { get; set; } = "";

    public GenerationSnapshot Snapshot { get; set; }

    public Tick Tick { get; set; }

    /// <summary>Records the inspection scope so replay does not infer ordinary findings from a deliberately partial emergency observation.</summary>
    public bool Immediate { get; set; }

    public RoundsFacts Facts { get; set; } = new();

    public RoundsLatches Latches { get; set; } = new();

    /// <summary>The staged policy the review detected against.</summary>
    public RoundsPolicy Policy { get; set; } = new();

    /// <summary>The journal's rounds after this one filed.</summary>
    public StoreRounds? Review { get; set; }

    /// <summary>
    /// The colony reading the review took, the planners' inputs beyond Facts (site cells, planning definitions,
    /// power topology, rooms, bounds); its Facts are left empty here and restored from Facts on load, and its Zones
    /// and Window are not recorded. Null in a recording that predates it.
    /// </summary>
    public ColonyProjection? Projection { get; set; }

    /// <summary>Replays the review's need detection over the recorded facts.</summary>
    public RoundsFindings Detect() =>
        Immediate
            ? PolicyFunctions.InspectImmediateRounds(Facts, Latches, Policy)
            : PolicyFunctions.InspectRounds(Facts, Latches, Policy);

    /// <summary>The replayed review's assessment of one goal.</summary>
    public RoundsAssessment Assessment(ConcernId id)
    {
        var needs = Detect();
        foreach (var a in needs.All())
        {
            if (a.Id == id)
            {
                return a;
            }
        }
        throw new DomainException("snapshot: review assessed no " + id.Value);
    }

    /// <summary>Drops the planning window's site cells, most of a recording's size: a committed snapshot keeps them only when its test runs a site search.</summary>
    public void TrimCells()
    {
        if (Projection is not null)
        {
            Projection = Projection with { Cells = [] };
        }
    }

    /// <summary>
    /// A recording from before the item facts carries none: it replays with Core's numbers, the game it was
    /// recorded on; and one whose gear census holds a pawn with no loadout model reads that census as unknown (what
    /// a live read would refuse). Call it on a deserialised recording, as Go's Load did.
    /// </summary>
    public void Normalize()
    {
        if (Immediate != (Review?.Immediate ?? Immediate))
        {
            throw new DomainException("snapshot: inspection scope differs from review");
        }
        if (Facts.Gear.TryGetValue(out var gear) && gear.Pawns.Any(p => !p.LoadoutModel.IsKnown && p.ModelRefusal == "" && !p.Blocked))
        {
            Facts = Facts with { Gear = Unknown<GearObservation>() };
        }
        if (Facts.Items.Market is null)
        {
            var items = PolicyFunctions.CoreItemFacts();
            Facts = Facts with { Items = items, MedicalReserve = Facts.MedicalReserve with { Catalog = items } };
        }
        if (Projection is not null)
        {
            Projection = Projection with { Facts = Facts };
        }
    }
}

public static partial class SnapshotPackage
{
    /// <summary>The snapshot of an enabled review's result; null when the review detected nothing (disabled).</summary>
    public static SnapshotRounds? FromReview(GenerationSnapshot current, Tick tick, StoreRoundsResult result, ColonyProjection reading)
    {
        if (result.Detection is not { } detection)
        {
            return null;
        }
        var review = result.Review;
        // Facts ride once; the zone read holds native protobuf messages and the window only repeats Region and
        // Cells, so neither is recorded. The bed price is a function over the catalog and cannot be: a replay
        // prices no bed.
        var none = new ColonyProjection();
        reading = reading with { Facts = none.Facts, Zones = none.Zones, Window = none.Window, BedPrice = none.BedPrice };
        return new SnapshotRounds
        {
            Projection = reading,
            Recorded = Provenance(current, tick),
            Snapshot = current,
            Tick = tick,
            Immediate = review.Immediate,
            Facts = detection.Facts,
            Latches = detection.Latches,
            Policy = detection.Policy,
            Review = review,
        };
    }
}
