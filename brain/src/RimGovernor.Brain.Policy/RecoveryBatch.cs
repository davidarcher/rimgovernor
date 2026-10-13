using System.Collections.ObjectModel;

namespace RimGovernor.Brain.Policy;

// Roof-first batch sequencing, executed by the clearance planner. The recovery queue's admitted and queued
// removals come down as one batch, not one building per review: when the batch's joint roof check
// (RoofSupportGrid.RoofBlocker) would leave unsupported roof, the removable (non-thick) roofs over the area
// are designated off first, and the batch is admitted once they are down. Roof removal is the existing
// remove_roof action. The function is stateless: a roof already down just stops blocking, so the next
// review's call reaches the removal stage. Whatever still blocks after the roofs (a thick roof, an unknown
// cell) trims the batch, one holding the roof_support_risk word.
//
// The native roof-collapse buffer (IsMarkedToCollapse) is not in the mirror, so RoofSupportGrid.CollapsePending
// stays null here: a short-lived guard, since native re-runs its own counterfactual roof check at admission
// and refuses a removal that would collapse, so a pending collapse costs a refused removal, not a collapse.

/// <summary>The roof rules by RoofDef name; a roof whose def is not in them is left alone.</summary>
public sealed class RoofRules(IDictionary<string, RoofRule> rules) : ReadOnlyDictionary<string, RoofRule>(rules)
{
}

/// <summary>What the batch does this review.</summary>
public enum RecoveryBatchStage
{
    /// <summary>Nothing to do (no candidate, or all held).</summary>
    [WireName("none")]
    None,

    /// <summary>Designates the Roof cells off; no removal yet.</summary>
    [WireName("roof")]
    Roof,

    /// <summary>Admits Batch for deconstruction.</summary>
    [WireName("removal")]
    Removal,
}

/// <summary>One queue removal: its census load id and a cell it occupies (the census Minimum).</summary>
public sealed record RecoveryBatchTarget(string ID, Cell Cell);

/// <summary>
/// The batch input. Targets are in queue rank order. Rules decide which roofs are removable. Max bounds the
/// batch (zero reads DefaultRecoveryBatch).
/// </summary>
public sealed record RecoveryBatchRequest
{
    public required RoofSupportGrid Grid { get; init; }

    public RoofRules Rules { get; init; } = new(new Dictionary<string, RoofRule>());

    public IReadOnlyList<RecoveryBatchTarget> Targets { get; init; } = [];

    public int Max { get; init; }
}

/// <summary>A target the batch refuses, in the RemoteHoldReason vocabulary.</summary>
public sealed record RecoveryBatchHold(string ID, string Reason);

/// <summary>
/// The verdict: the stage, the roof cells to take off (stage roof), the target ids to deconstruct together
/// (stage removal), and the refused targets. Blocker is the first joint-check word that trimmed or staged
/// anything.
/// </summary>
public sealed record RecoveryBatch
{
    public RecoveryBatchStage Stage { get; init; }

    public EquatableArray<Cell> Roof { get; init; }

    public EquatableArray<string> Batch { get; init; }

    public EquatableArray<RecoveryBatchHold> Held { get; init; }

    public string Blocker { get; init; } = "";
}

public static partial class PolicyFunctions
{
    /// <summary>Bounds one removal batch.</summary>
    public const int DefaultRecoveryBatch = 12;

    /// <summary>
    /// The queue's admitted and queued salvage entries in rank order, resolved against the fresh census rows.
    /// A row that has since gained a hold of its own (a native verdict, no evidence, an unsafe route) drops
    /// out, so the review's queue never overrides the fresh read. Loot is a haul, not a removal.
    /// </summary>
    public static IReadOnlyList<RecoveryBatchTarget> RecoveryBatchTargets(RecoveryQueue q, IReadOnlyList<ClearanceTarget> rows)
    {
        var byId = new Dictionary<string, ClearanceTarget>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            byId[r.EntityID] = r;
        }
        var output = new List<RecoveryBatchTarget>();
        foreach (var e in q.Entries)
        {
            if (e.Kind != RemoteWorkKind.Salvage || (e.Status != RecoveryStatus.Admitted && e.Status != RecoveryStatus.Queued))
            {
                continue;
            }
            if (!byId.TryGetValue(e.ID, out var r))
            {
                continue;
            }
            var (t, ok) = RecoveryClearanceThing(r, false);
            if (ok && RecoveryHold(t, new RecoveryRequest()).Length == 0)
            {
                output.Add(new RecoveryBatchTarget(e.ID, r.Minimum));
            }
        }
        return output;
    }

    /// <summary>The mirror building with load id at cell.</summary>
    private static (Thing Thing, bool Ok) BuildingAt(RoofSupportGrid g, Cell c, string loadId)
    {
        var row = g.Cell(c);
        if (row is null)
        {
            return (default!, false);
        }
        foreach (var t in row.Things)
        {
            if (t.Category == ThingCategory.Building && t.LoadId() == loadId)
            {
                return (t, true);
            }
        }
        return (default!, false);
    }

    /// <summary>
    /// The roofed, removable cells within the support radius of the building's footprint, bar a cell carrying
    /// a player thing (the colony's own roof stays).
    /// </summary>
    private static List<Cell> RemovableRoofs(RoofSupportGrid g, Cell origin, ulong id, RoofRules rules)
    {
        var seen = new HashSet<Cell>();
        var output = new List<Cell>();
        var r = (int)g.Radius;
        foreach (var f in g.Footprint(origin, id))
        {
            for (var dz = -r; dz <= r; dz++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    var c = new Cell(f.X + dx, f.Z + dz);
                    if (seen.Contains(c) || !g.Within(c, f) || !g.InBounds(c))
                    {
                        continue;
                    }
                    seen.Add(c);
                    var row = g.Cell(c);
                    if (row is null)
                    {
                        continue;
                    }
                    if (!row.Roofed.TryGetValue(out var roofed) || !roofed)
                    {
                        continue;
                    }
                    if (!row.Roof.TryGetValue(out var name) || !rules.TryGetValue(name, out var rule) || !rule.Removable())
                    {
                        continue;
                    }
                    if (!row.Things.Any(t => t.Faction == ThingFaction.Player))
                    {
                        output.Add(c);
                    }
                }
            }
        }
        return output;
    }

    /// <summary>
    /// The roof-first batch over the request. Targets are taken greedily in rank order: one is kept while the
    /// kept set plus it passes the joint roof check. One the check refuses because of unsupported roof stages
    /// the removable roofs around it; any other refusal, or an unsupported roof nothing can take off, holds it
    /// roof_support_risk. With roofs to take off the stage is roof and no removal is admitted until they are
    /// down.
    /// </summary>
    public static RecoveryBatch PlanRecoveryBatch(RecoveryBatchRequest req)
    {
        var limit = req.Max <= 0 ? DefaultRecoveryBatch : req.Max;
        var batch = new List<string>();
        var held = new List<RecoveryBatchHold>();
        var roof = new List<Cell>();
        var blockerWord = "";
        var kept = new List<RemovedBuilding>();
        var roofSet = new HashSet<Cell>();
        foreach (var t in req.Targets)
        {
            if (batch.Count >= limit)
            {
                break;
            }
            var (b, ok) = BuildingAt(req.Grid, t.Cell, t.ID);
            if (!ok)
            {
                held.Add(new RecoveryBatchHold(t.ID, RemoteHoldRoofSupport));
                continue;
            }
            var next = new List<RemovedBuilding>(kept) { new() { Cell = t.Cell, Id = b.Id } };
            var (blocker, _) = req.Grid.RoofBlocker(next);
            if (blocker.Length == 0)
            {
                kept = next;
                batch.Add(t.ID);
                continue;
            }
            if (blockerWord.Length == 0)
            {
                blockerWord = blocker;
            }
            var cells = blocker == RoofBlockerUnsupported ? RemovableRoofs(req.Grid, t.Cell, b.Id, req.Rules) : [];
            if (cells.Count == 0)
            {
                held.Add(new RecoveryBatchHold(t.ID, RemoteHoldRoofSupport));
                continue;
            }
            foreach (var c in cells)
            {
                if (roofSet.Add(c))
                {
                    roof.Add(c);
                }
            }
        }
        var stage = RecoveryBatchStage.None;
        if (roof.Count > 0)
        {
            roof = roof.OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
            stage = RecoveryBatchStage.Roof;
            batch.Clear();
        }
        else if (batch.Count > 0)
        {
            stage = RecoveryBatchStage.Removal;
        }
        return new RecoveryBatch
        {
            Stage = stage,
            Roof = EquatableArray.From(roof),
            Batch = EquatableArray.From(batch),
            Held = EquatableArray.From(held),
            Blocker = blockerWord,
        };
    }
}
