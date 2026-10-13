using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// Foreign things in a reconciliation: the things on the room's ground that are not the colony's own
// (SiteCell.Things) are diffed against the plan by one obstruction policy: claim a ruin as wall where it fits
// the ring, else minify what packs, else deconstruct a building or cut an impassable plant, and move a
// haulable item. Natural rock and mineables stay with the dig path. A thing the policy refuses is a hold with a
// named reason; its cells wait like a cell being cleared. The kinds are operations only: the executor maps each
// to an existing action, and the native placement preview stays the legality authority.

/// <summary>A foreign thing the policy leaves standing and why: "ancient_danger", "casket" or "not_deconstructible".</summary>
public sealed record ReconcileHold
{
    public Cell Cell { get; init; }

    public string Def { get; init; } = "";

    public string Reason { get; init; } = "";
}

/// <summary>
/// What the ground's foreign things owe: the items, the cells they block (cleared or held), the ring cells a
/// claim keeps as wall and the holds.
/// </summary>
internal sealed class ForeignWork
{
    public List<ReconcileItem> Items { get; } = [];

    public HashSet<Cell> Blocked { get; } = [];

    public HashSet<Cell> Claimed { get; } = [];

    public List<ReconcileHold> Holds { get; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The cut wave of a furniture slot the native preview refused naming a plant: every plant, passable or
    /// not, on the slot's footprint or interaction cell that the refusal names by def (or any plant when it
    /// names only a category). A slot has no outdoor stand-in, so the plant is a foreign obstruction like an
    /// impassable one on a room's ground. The preview stays the legality authority: nothing is cut unless it
    /// refused on a plant.
    /// </summary>
    public static IReadOnlyList<ClearanceTarget> SlotPlantCuts(IReadOnlyList<SiteCell> cells, InteriorPiece piece, IReadOnlyList<PlacementBlocker> blockers)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        var anyPlant = false;
        foreach (var b in blockers)
        {
            if (b.DefName.Length != 0)
            {
                named.Add(b.DefName);
            }
            else if (string.Equals(b.Category, "plant", StringComparison.OrdinalIgnoreCase))
            {
                anyPlant = true;
            }
        }
        var slot = new HashSet<Cell>(RectCells(piece.Rect));
        var (interaction, ok) = piece.Interaction();
        if (ok)
        {
            slot.Add(interaction);
        }
        var output = new List<ClearanceTarget>();
        foreach (var sc in cells)
        {
            if (!slot.Contains(sc.Cell))
            {
                continue;
            }
            foreach (var t in sc.Things)
            {
                if (t.Category != ThingCategory.Plant || t.Id == 0 || (!named.Contains(t.Def) && !anyPlant))
                {
                    continue;
                }
                output.Add(new ClearanceTarget
                {
                    EntityID = t.LoadId(),
                    DefName = t.Def,
                    Minimum = sc.Cell,
                    Maximum = sc.Cell,
                    Class = "foreign",
                    InHome = true,
                    Designated = t.Has(ThingFlags.Designated),
                });
            }
        }
        return output.OrderBy(t => t.Minimum.Z).ThenBy(t => t.Minimum.X).ThenBy(t => t.EntityID, StringComparer.Ordinal).ToList();
    }

    private static ForeignWork ForeignThings(ReconcileInput input, Region ring, string wallDef, HashSet<Cell> doorWanted)
    {
        var w = new ForeignWork();
        var ground = input.Room.GroundRegion();
        var index = new Dictionary<(ulong Id, string Def, Cell Cell), ReconcileItem>();

        // Place records t on c as an item of kind, widening the target of a thing that spans cells.
        void Place(OpKind kind, Thing t, Cell c, bool packable)
        {
            var key = (t.Id, t.Def, t.Id == 0 ? c : default);
            // A stack on the door cell is cleanup (vanilla hauling takes it): the native preview, not the diff,
            // decides whether the door goes up over it, so a room is never held unfinished (or unreachable, door
            // unbuilt) by an item.
            if (kind != OpKind.HaulOut || !doorWanted.Contains(c))
            {
                w.Blocked.Add(c);
            }
            if (index.TryGetValue(key, out var existing))
            {
                var tg = existing.Target;
                existing.Target = tg with
                {
                    Minimum = new Cell(Math.Min(tg.Minimum.X, c.X), Math.Min(tg.Minimum.Z, c.Z)),
                    Maximum = new Cell(Math.Max(tg.Maximum.X, c.X), Math.Max(tg.Maximum.Z, c.Z)),
                };
                return;
            }
            var item = new ReconcileItem
            {
                Kind = kind,
                Cell = c,
                Ready = true,
                Foreign = true,
                Target = new ClearanceTarget
                {
                    EntityID = t.LoadId(),
                    DefName = t.Def,
                    Minimum = c,
                    Maximum = c,
                    Class = "foreign",
                    Deconstructible = t.Has(ThingFlags.Deconstructible),
                    Count = t.Count,
                    InHome = true,
                    Designated = t.Has(ThingFlags.Designated),
                    Packable = packable,
                    AncientDanger = t.Has(ThingFlags.AncientDanger),
                },
            };
            index[key] = item;
            w.Items.Add(item);
        }

        void Hold(Thing t, Cell c, string reason)
        {
            w.Blocked.Add(c);
            w.Holds.Add(new ReconcileHold { Cell = c, Def = t.Def, Reason = reason });
        }

        foreach (var sc in input.Cells ?? [])
        {
            var c = sc.Cell;
            if (!ground.Contains(c))
            {
                continue;
            }
            var claimCell = wallDef.Length != 0 && ring.Contains(c) && !doorWanted.Contains(c) && !input.Room.Outdoor && sc.ClaimableRuin() == wallDef && sc.RuinHold().Length == 0;
            foreach (var t in sc.Things)
            {
                if (t.Category == ThingCategory.Plant)
                {
                    if (t.Has(ThingFlags.Impassable))
                    {
                        Place(OpKind.Cut, t, c, false);
                    }
                }
                else if (t.Category == ThingCategory.Item)
                {
                    if (ring.Contains(c) && !doorWanted.Contains(c))
                    {
                        // The builder hauls any item aside once the wall's blueprint is placed over it
                        // (GenConstruct.HandleBlockingThingJob): a ring cell never waits on an item, and the
                        // native preview stays the legality authority.
                        continue;
                    }
                    if (t.Has(ThingFlags.Haulable) && !t.Has(ThingFlags.Forbidden))
                    {
                        Place(OpKind.HaulOut, t, c, false);
                    }
                    else if (t.Has(ThingFlags.Haulable) && doorWanted.Contains(c))
                    {
                        // Nothing hauls a forbidden stack: it is named, not blocking, so the door goes on to
                        // its placement and the wait shows what sits on it.
                        w.Holds.Add(new ReconcileHold { Cell = c, Def = t.Def, Reason = "forbidden_item" });
                    }
                }
                else if (t.Category == ThingCategory.Building && t.Faction != ThingFaction.Player && !t.Has(ThingFlags.Blueprint) && !t.Has(ThingFlags.Frame))
                {
                    if (t.Has(ThingFlags.AncientDanger))
                    {
                        Hold(t, c, "ancient_danger");
                    }
                    else if (t.Building is not null && t.Building.Casket.Count > 0)
                    {
                        Hold(t, c, "casket");
                    }
                    else if (claimCell && t.Has(ThingFlags.Edifice))
                    {
                        // A claimed ruin stands as wall: nothing to raise or clear.
                        var was = w.Blocked.Contains(c);
                        w.Claimed.Add(c);
                        Place(OpKind.Claim, t, c, false);
                        if (!was)
                        {
                            w.Blocked.Remove(c);
                        }
                    }
                    else if (t.Has(ThingFlags.Minifiable))
                    {
                        Place(OpKind.Pack, t, c, true);
                    }
                    else if (t.Has(ThingFlags.Deconstructible))
                    {
                        Place(OpKind.FurnitureOut, t, c, false);
                    }
                    else if (t.Has(ThingFlags.Edifice))
                    {
                        // Natural rock or a mineable: the dig path's.
                    }
                    else
                    {
                        Hold(t, c, "not_deconstructible");
                    }
                }
            }
        }
        var sorted = w.Items.OrderBy(i => i.Target.Minimum.Z).ThenBy(i => i.Target.Minimum.X).ThenBy(i => i.Target.EntityID, StringComparer.Ordinal).ToList();
        w.Items.Clear();
        w.Items.AddRange(sorted);
        return w;
    }
}
