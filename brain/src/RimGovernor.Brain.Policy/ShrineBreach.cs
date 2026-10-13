using DomainPawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One shrine's judgement as the review journals it: Reason is a ShrineHold* constant, ShrineHoldGuardsAlive
/// after the breach, or ShrineReady; Wall names the chosen breach wall when one was chosen. A row with a
/// Casket is that casket's CasketDecision instead.
/// </summary>
public sealed record ShrineHold
{
    public string Shrine { get; init; } = "";

    public string Reason { get; init; } = "";

    public string Wall { get; init; } = "";

    public string Casket { get; init; } = "";

    /// <summary>Occupant rows name a released humanlike and the colony decision on it (OccupantDecision).</summary>
    public string Occupant { get; init; } = "";

    /// <summary>
    /// Marks a shrine row with what the shrine planner's last step did with it: "held" for the shrine it held
    /// on, "skipped" for a candidate it passed over; empty for a shrine it never judged.
    /// </summary>
    public string Planner { get; init; } = "";
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The breach goal: a sealed shrine whose room touches Home is a deficit until it is open and its guards
    /// are down. The goal drafts the readiness squad behind the trap line, deconstructs the chosen wall in
    /// place and lets ActiveCombat fight what pops.
    /// </summary>
    public static readonly ConcernId ClearAncientShrine = new("ClearAncientShrine");

    public const string ShrineReady = "ready";
    public const string ShrineHoldGuardsAlive = "guards_alive";

    // Casket decisions: the default policy never opens a casket.
    public const string CasketClaim = "claim";
    public const string CasketLeaveSealed = "leave_sealed";
    public const string CasketClaimed = "claimed";
    public const string CasketHoldSealed = "shrine_sealed";

    /// <summary>
    /// The default casket policy: an empty casket the player does not own yet is claimed; a filled one is left
    /// sealed (the hostile ancients inside are a risk with no upside the colony needs); a casket already the
    /// player's is done. Both hold shrine_sealed while the room is sealed and guards_alive while a guard stands.
    /// </summary>
    public static string CasketDecision(ShrineCasket casket, AncientShrine shrine) =>
        CasketDecisionUnder(casket, shrine, new ShrinePolicy());

    /// <summary>
    /// CasketDecision under an operator policy: a filled casket of an open, guard-free shrine takes the
    /// shrine's Opening decision: open, or the readiness hold it is left sealed on.
    /// </summary>
    public static string CasketDecisionUnder(ShrineCasket casket, AncientShrine shrine, ShrinePolicy policy)
    {
        if (shrine.Sealed)
        {
            return CasketHoldSealed;
        }
        if (shrine.GuardsAlive())
        {
            return ShrineHoldGuardsAlive;
        }
        if (casket.HasContents && policy.Opening.TryGetValue(shrine.Id, out var opening) && opening != "")
        {
            return opening;
        }
        if (casket.HasContents)
        {
            return CasketLeaveSealed;
        }
        if (casket.PlayerClaimed)
        {
            return CasketClaimed;
        }
        return CasketClaim;
    }

    /// <summary>The empty caskets the breach goal owes a claim on: those of an open, guard-free shrine touching Home, in casket identity order per shrine.</summary>
    public static Dictionary<string, List<ShrineCasket>> ShrineClaimTargets(IReadOnlyList<AncientShrine> rows)
    {
        var output = new Dictionary<string, List<ShrineCasket>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!row.InHome || row.Sealed || !row.GuardsKnown || row.GuardsAlive())
            {
                continue;
            }
            var caskets = row.Caskets.Where(c => CasketDecision(c, row) == CasketClaim)
                .OrderBy(c => c.EntityId, StringComparer.Ordinal)
                .ToList();
            if (caskets.Count == 0)
            {
                continue;
            }
            output[row.Id] = caskets;
        }
        return output;
    }

    // Squad geometry behind the breach: defenders stand ShrineStandDistance cells straight out from the wall's
    // outside cell, never nearer than ShrineStandMinimum along that line (the trap line lies between), never
    // further than the trap radius the readiness gate counted.
    internal const int ShrineStandDistance = 8;
    internal const int ShrineStandMinimum = 5;

    /// <summary>
    /// The shrines the breach goal owes work on: those touching Home that are still sealed, open with a guard
    /// seen standing, open and guard-free with an empty casket still to claim, or with a filled casket the
    /// policy opens. A shrine open but fogged (nobody has looked in) is not a target: exploring is not this
    /// goal's, and ActiveCombat answers a guard the moment it is seen. Stable by identity.
    /// </summary>
    public static List<string> ShrineClearanceTargets(IReadOnlyList<AncientShrine> rows, ShrinePolicy policy)
    {
        var output = new List<string>();
        var claims = ShrineClaimTargets(rows);
        var opens = ShrineOpenTargets(rows, policy);
        foreach (var row in rows)
        {
            if (!row.InHome)
            {
                continue;
            }
            if (row.Sealed || (row.GuardsKnown && row.GuardsAlive()) || claims.ContainsKey(row.Id) || opens.ContainsKey(row.Id))
            {
                output.Add(row.Id);
            }
        }
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    /// <summary>Names why one shrine is not breached now: the readiness gate's reason while sealed, guards_alive once open, ready otherwise.</summary>
    public static string ShrineHoldReason(AncientShrine shrine, ShrineReadiness readiness)
    {
        if (!shrine.Sealed && shrine.GuardsAlive())
        {
            return ShrineHoldGuardsAlive;
        }
        if (readiness.Ready)
        {
            return ShrineReady;
        }
        return readiness.Reason;
    }

    /// <summary>
    /// The part of the readiness squad the breach drafts: all of it while another colonist is free to do the
    /// deconstruct job, otherwise all but the last (the least useful, a melee pawn when any shooter stands).
    /// Readiness guarantees at least two, so one always stands.
    /// </summary>
    public static List<DomainPawnId> ShrineBreachDrafts(IReadOnlyList<DomainPawnId> squad, int colonists)
    {
        var output = new List<DomainPawnId>(squad);
        if (colonists <= output.Count && output.Count > 1)
        {
            output.RemoveAt(output.Count - 1);
        }
        return output;
    }

    /// <summary>
    /// Picks one standing cell per drafted defender behind the trap line: cells at least ShrineStandMinimum
    /// along the outward line from the wall's outside cell and within the trap radius, never a trap cell,
    /// nearest the ideal point ShrineStandDistance out first. A defender with no cell left stands where it is
    /// (drafted, not moved).
    /// </summary>
    public static Dictionary<DomainPawnId, Cell> ShrineBreachPositions(
        ShrineBreachWall wall, IReadOnlyList<DomainPawnId> squad, IReadOnlyList<Cell> standing, IReadOnlyList<Cell> traps)
    {
        int dx = Sign(wall.Outside.X - wall.Cell.X), dz = Sign(wall.Outside.Z - wall.Cell.Z);
        if ((dx == 0 && dz == 0) || (dx != 0 && dz != 0))
        {
            return [];
        }
        var trapped = new HashSet<Cell>(traps);
        var ideal = new Cell(wall.Outside.X + (dx * ShrineStandDistance), wall.Outside.Z + (dz * ShrineStandDistance));
        var cells = new List<Cell>();
        var seen = new HashSet<Cell>();
        foreach (var cell in standing)
        {
            if (seen.Contains(cell) || trapped.Contains(cell))
            {
                continue;
            }
            var along = ((cell.X - wall.Outside.X) * dx) + ((cell.Z - wall.Outside.Z) * dz);
            if (along < ShrineStandMinimum || SquaredDistance(cell, wall.Outside) > (long)(ShrineTrapRadius * ShrineTrapRadius))
            {
                continue;
            }
            seen.Add(cell);
            cells.Add(cell);
        }
        var sorted = cells
            .OrderBy(c => SquaredDistance(c, ideal))
            .ThenBy(c => c.X)
            .ThenBy(c => c.Z)
            .ToList();
        var output = new Dictionary<DomainPawnId, Cell>();
        for (var i = 0; i < squad.Count; i++)
        {
            if (i >= sorted.Count)
            {
                break;
            }
            output[squad[i]] = sorted[i];
        }
        return output;
    }

    internal static int Sign(int v) => v < 0 ? -1 : v > 0 ? 1 : 0;
}
