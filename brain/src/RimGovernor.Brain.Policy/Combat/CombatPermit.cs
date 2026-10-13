namespace RimGovernor.Brain.Policy;

/// <summary>
/// Classifies an acting permit the fight may call: aid (reinforcements land at a cell) or a strike (a
/// bombardment lands on the hostiles). Anything else, laborers and trade included, is not a fight call.
/// </summary>
internal enum PermitFightKind
{
    NotFight = 0,
    AidCall,
    StrikeCall,
}

public static partial class PolicyFunctions
{
    internal static PermitFightKind PermitFightClass(string name)
    {
        if (name.Contains("Strike", StringComparison.Ordinal) || name.Contains("Bombard", StringComparison.Ordinal))
        {
            return PermitFightKind.StrikeCall;
        }
        if (PermitCategoryOf(new RoyalPermit { Name = name }) == PermitCategory.Aid && !name.Contains("Laborer", StringComparison.Ordinal))
        {
            return PermitFightKind.AidCall;
        }
        return PermitFightKind.NotFight;
    }

    internal static string PermitKey(PawnId pawn, string faction, string permit) => pawn.Value + "/" + faction + "/" + permit;

    /// <summary>Reports a held acting permit that is off cooldown and affordable per the royalty read; every unread fact holds the call.</summary>
    internal static bool PermitReady(RoyaltyFacts f, RoyalHolding h, string name)
    {
        if (!f.Permits.TryGetValue(name, out var permit))
        {
            return false;
        }
        if (!permit.Acts.TryGetValue(out var acts) || !acts)
        {
            return false;
        }
        var costKnown = permit.FavorCost.TryGetValue(out var cost);
        var favorKnown = h.Favor.TryGetValue(out var favor);
        if (!costKnown || !favorKnown || favor < cost)
        {
            return false;
        }
        return h.Cooldowns.TryGetValue(name, out var cooldown) && cooldown.RemainingTicks.TryGetValue(out var remaining) && remaining == 0;
    }

    /// <summary>
    /// This stop's permit calls: with a known armed squad outmatched, each held, ready aid or strike permit
    /// whose holder is a standing colonist and for which a target exists. A strike lands on the densest hostile
    /// clump no colonist stands near; aid lands on the defender nearest the squad's centre. Each holder-permit
    /// is called once per fight.
    /// </summary>
    internal static (CombatMemory Memory, IReadOnlyList<CombatOrder> Calls) PermitCalls(CombatView view, CombatMemory m)
    {
        if (!view.Royalty.TryGetValue(out var royalty) || view.Hunt || !OutmatchedKnown(view))
        {
            return (m, []);
        }
        var down = DownPawns(view);
        var calls = new List<CombatOrder>();
        var permitted = m.Permitted.ToList();
        foreach (var holder in SortedHolders(royalty.Holders))
        {
            var pawn = holder;
            if (down.Contains(pawn) || !view.Defenders.Any(d => d.Id == pawn))
            {
                continue;
            }
            foreach (var h in royalty.Holders[holder])
            {
                foreach (var name in h.Permits)
                {
                    var kind = PermitFightClass(name);
                    var key = PermitKey(pawn, h.FactionDef, name);
                    if (kind == PermitFightKind.NotFight || permitted.Contains(key) || !PermitReady(royalty, h, name))
                    {
                        continue;
                    }
                    var (cell, found) = kind == PermitFightKind.StrikeCall ? StrikeCell(view) : AidCell(view);
                    if (!found)
                    {
                        continue;
                    }
                    permitted.Add(key);
                    calls.Add(new CombatOrder { Pawn = pawn, Kind = CombatOrderKind.Permit, Cell = cell, Faction = h.FactionDef, Permit = name, Reason = CombatOrderReason.Permit });
                }
            }
        }
        return (m with { Permitted = EquatableArray.From(permitted) }, calls);
    }

    /// <summary>Outmatched with a known eligible armed defender: with none the comparison measures missing facts, not the fight.</summary>
    internal static bool OutmatchedKnown(CombatView view) =>
        view.Defenders.Any(d => SquadDefenderEligible(d) && Positive(d.Armed)) && Outmatched(view);

    /// <summary>The cells of the live hostile pawns, in view order.</summary>
    internal static List<Cell> LiveHostileCells(CombatView view)
    {
        var cells = new List<Cell>();
        foreach (var p in LiveHostiles(view))
        {
            p.Cell.TryGetValue(out var c);
            cells.Add(c);
        }
        return cells;
    }

    /// <summary>The hostile cell with the most hostiles within CampClumpRadius, skipping cells near a colonist (first on a tie).</summary>
    internal static (Cell Cell, bool Ok) StrikeCell(CombatView view)
    {
        var cells = LiveHostileCells(view);
        var ours = ColonistCells(view);
        Cell best = default;
        var most = 0;
        foreach (var c in cells)
        {
            if (NearAny(ours, c))
            {
                continue;
            }
            var n = 0;
            foreach (var o in cells)
            {
                if (Dist(c, o) <= CampClumpRadius)
                {
                    n++;
                }
            }
            if (n > most)
            {
                best = c;
                most = n;
            }
        }
        return (best, most > 0);
    }

    /// <summary>The known defender cell nearest the squad's mean cell.</summary>
    internal static (Cell Cell, bool Ok) AidCell(CombatView view)
    {
        var ours = ColonistCells(view);
        if (ours.Count == 0)
        {
            return (default, false);
        }
        double x = 0;
        double z = 0;
        foreach (var c in ours)
        {
            x += c.X;
            z += c.Z;
        }
        x /= ours.Count;
        z /= ours.Count;
        Cell best = default;
        var nearest = double.MaxValue;
        foreach (var c in ours)
        {
            var d = Math.Sqrt(((c.X - x) * (c.X - x)) + ((c.Z - z) * (c.Z - z)));
            if (d < nearest)
            {
                best = c;
                nearest = d;
            }
        }
        return (best, true);
    }
}
