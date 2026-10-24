using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Mirror;
using RimGovernor.Contracts.Wire.Observations;
using DomainCell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Contains actual native context, not controller authority. The runtime binds
/// these facts to its captured direction and plan after validation.
/// </summary>
public sealed record EmergencyObservation(ObservationContext? Context, EmergencyFacts Facts);

/// <summary>
/// The routine census of one frame, each section decoded. Every row has the frame's tick,
/// so no section is checked against another. A null section is one the frame does not carry.
/// </summary>
public sealed record RoundsFrame
{
    public ObservationContext? Context { get; init; }

    public ColonyFactsSnapshot? Colony { get; init; }

    public EmergencyObservation Emergency { get; init; } = new(null, new EmergencyFacts());

    /// <summary>The census colonists' rows of the pawn table, as the routine list read answers them; null without a complete census.</summary>
    public PawnSnapshot? Pawns { get; init; }

    public PrisonerCensus? Population { get; init; }

    /// <summary>The research section a decoded routine frame always carries; null only on the empty carrier frame a room reconcile passes.</summary>
    public ResearchSnapshot? Research { get; init; }

    public TradersRead? Traders { get; init; }

    public WorldProgressionRead? Quests { get; init; }

    /// <summary>The primary ideoligion with the catalog's defs; null when the frame carries no ideology section.</summary>
    public Ideoligion? Ideology { get; init; }

    /// <summary>Whether the Ideology expansion is installed; unknown when the frame does not say.</summary>
    public Fact<bool> IdeologyActive { get; init; }

    /// <summary>
    /// The frame's all-status player building table, whose blueprint and frame rows carry their undelivered
    /// material; null when the frame carries none.
    /// </summary>
    public BuildingCensus? Buildings { get; init; }

    /// <summary>The frame's keyed tables every other section's references resolve against: sites by id and the pawn table.</summary>
    public Tables Tables { get; init; } = new(default, default, default);

    public ZonesRead? Zones { get; init; }


    /// <summary>The indoor room census.</summary>
    public RoomsSnapshot? Rooms { get; init; }

    /// <summary>The census rooms' cells resolved against the newest frame grid.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DomainCell>> RoomCells { get; init; } = new Dictionary<string, IReadOnlyList<DomainCell>>(StringComparer.Ordinal);

    /// <summary>The bench bill census, whose rows carry live bill jobs' ingredient reservations.</summary>
    public BillsSnapshot? Bills { get; init; }
}

// The frame stream, its seeding and the combat frame validators are wire plumbing and not ported.
public static class RoundsFrameRules
{
    /// <summary>Whether watermark <paramref name="a"/> is strictly before <paramref name="b"/>.</summary>
    public static bool CombatBefore(Watermark? a, Watermark? b)
    {
        var aTick = a?.Tick ?? 0;
        var bTick = b?.Tick ?? 0;
        return aTick != bTick ? aTick < bTick : (a?.Seq ?? 0) < (b?.Seq ?? 0);
    }
}
