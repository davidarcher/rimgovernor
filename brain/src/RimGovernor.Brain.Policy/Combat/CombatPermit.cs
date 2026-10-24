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

public static partial class CombatPermitRules
{
    internal static PermitFightKind PermitFightClass(string name)
    {
        if (name.Contains("Strike", StringComparison.Ordinal) || name.Contains("Bombard", StringComparison.Ordinal))
        {
            return PermitFightKind.StrikeCall;
        }
        if (PermitChoiceRules.PermitCategoryOf(name) == PermitCategory.Aid && !name.Contains("Laborer", StringComparison.Ordinal))
        {
            return PermitFightKind.AidCall;
        }
        return PermitFightKind.NotFight;
    }

    internal static string PermitKey(PawnId pawn, string faction, string permit) => pawn.Value + "/" + faction + "/" + permit;

    /// <summary>Reports a held royal-aid permit that is off cooldown and affordable per the royalty read; a passive permit is never ready.</summary>
    internal static bool PermitReady(RoyaltyFacts f, PawnRoyalHolding h, string name) =>
        f.Permits.TryGetValue(name, out var permit)
        && permit.Aid is { } aid
        && h.Favor >= aid.FavorCost
        && RoyalHoldingRules.PermitReady(h, name);

    /// <summary>
    /// This stop's permit calls: with a known armed squad outmatched, each held, ready aid or strike permit
    /// whose holder is a standing colonist and for which a target exists. A strike lands on the densest hostile
    /// clump no colonist stands near; aid lands on the defender nearest the squad's centre. Each holder-permit
    /// is called once per fight.
    /// </summary>
    internal static (CombatMemory Memory, IReadOnlyList<CombatOrder> Calls) PermitCalls(CombatView view, CombatMemory m)
    {
        if (view.Royalty is not { } royalty || view.Hunt || !OutmatchedKnown(view))
        {
            return (m, []);
        }
        var down = CombatManhunterRules.DownPawns(view);
        var calls = new List<CombatOrder>();
        var permitted = m.Permitted.ToList();
        foreach (var holder in TitleClaimRules.SortedHolders(royalty.Holders))
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
        view.Defenders.Any(d => SquadDefenseRules.SquadDefenderEligible(d) && d.Armed) && CombatWaitRules.Outmatched(view);

    /// <summary>The cells of the live hostile pawns, in view order.</summary>
    internal static List<Cell> LiveHostileCells(CombatView view)
    {
        var cells = new List<Cell>();
        foreach (var p in CombatCastRules.LiveHostiles(view))
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
        var ours = CombatMortarRules.ColonistCells(view);
        Cell best = default;
        var most = 0;
        foreach (var c in cells)
        {
            if (CombatMortarRules.NearAny(ours, c))
            {
                continue;
            }
            var n = 0;
            foreach (var o in cells)
            {
                if (CombatMortarRules.Dist(c, o) <= CombatMortarRules.CampClumpRadius)
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
        var ours = CombatMortarRules.ColonistCells(view);
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
