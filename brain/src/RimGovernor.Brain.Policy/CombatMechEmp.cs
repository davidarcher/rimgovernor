namespace RimGovernor.Brain.Policy;

// Go: combat_mech_emp.go

/// <summary>A mech EMP no longer stuns until <paramref name="Until"/>.</summary>
public readonly record struct EmpAdaptation(PawnId Pawn, Tick Until);

public static partial class PolicyFunctions
{
    /// <summary>
    /// How long a mech stays EMP-adapted once its stun ends: vanilla adapts it for 2200 ticks from the hit,
    /// and an EMP grenade (50 damage x 30 ticks) stuns it for 1500.
    /// </summary>
    internal const int EmpAdaptAfterStun = 700;

    /// <summary>How early blockers leave a stunned mech: time to step back to the inner line before it wakes.</summary>
    internal const int EmpDisengageTicks = 120;

    /// <summary>How near (Chebyshev) the choke a scyther counts as in it.</summary>
    internal const int EmpChokeReach = 2;

    /// <summary>How near a blocker a mech counts as on it.</summary>
    internal const int EmpNearBlocker = 2;

    /// <summary>
    /// Records every hostile mech seen stunned, until its EMP adaptation ends, and drops the expired records. A
    /// mech already recorded keeps its first Until: the stun it is adapted to is the first.
    /// </summary>
    internal static void NoteEmpAdapted(CombatView view, CombatMemory m)
    {
        m.EmpAdapted.RemoveAll(a => a.Until.Value <= view.Tick.Value);
        foreach (var h in RankThreats(view))
        {
            if (!IsMech(h) || h.StunTicks <= 0 || EmpAdapted(m, h.Id))
            {
                continue;
            }

            m.EmpAdapted.Add(new EmpAdaptation(h.Id, new Tick(view.Tick.Value + h.StunTicks + EmpAdaptAfterStun)));
        }

        m.EmpAdapted = PolicySort.ByLess(m.EmpAdapted, (a, b) => string.CompareOrdinal(a.Pawn.Value, b.Pawn.Value) < 0);
    }

    private static bool EmpAdapted(CombatMemory m, PawnId id) => m.EmpAdapted.Any(a => a.Pawn == id);

    /// <summary>
    /// The grenade step's ground cell. Against mechs an EMP only stuns scythers already in the choke, awake and
    /// not adapted; the blockers hold the choke, so when every hostile cell is too near them it aims at any cell
    /// whose blast still reaches such a scyther. Otherwise it is GrenadeTarget.
    /// </summary>
    private static Cell? GrenadeAim(CombatView view, CombatMemory m, CombatPawnState carrier, IReadOnlyList<CombatPawnState> hostiles, IReadOnlyList<Cell> colonists)
    {
        if (!carrier.WeaponFacts.EMP || !hostiles.Any(IsMech))
        {
            return GrenadeTarget(carrier, hostiles, colonists);
        }

        var ok = view.Layout.TryGetValue(out var layout);
        Cell choke = default;
        var cok = ok && layout!.Choke.TryGetValue(out choke);
        var fok = carrier.Cell.TryGetValue(out var from);
        var blast = carrier.WeaponFacts.Blast;
        if (!ok || !cok || !fok || blast <= 0)
        {
            return null;
        }

        var scythers = new List<Cell>();
        foreach (var h in hostiles)
        {
            if (h.Cell.TryGetValue(out var c) && !h.Dead && !h.Downed && h.Kind.StartsWith("Mech_Scyther", StringComparison.Ordinal)
                && h.StunTicks == 0 && !EmpAdapted(m, h.Id) && Chebyshev(c, choke) <= EmpChokeReach)
            {
                scythers.Add(c);
            }
        }

        if (scythers.Count == 0)
        {
            return null;
        }

        var reach = GrenadeReach(carrier);
        if (BestGround(from, reach, blast, scythers, scythers, colonists) is { } direct)
        {
            return direct;
        }

        var radius = (int)Math.Floor(blast);
        var around = new List<Cell>();
        foreach (var s in scythers)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dz = -radius; dz <= radius; dz++)
                {
                    around.Add(new Cell(s.X + dx, s.Z + dz));
                }
            }
        }

        return BestGround(from, reach, blast, around, scythers, colonists);
    }

    /// <summary>
    /// Pulls each blocker back to the inner line before a mech on it wakes from a stun: the stun ends within
    /// EmpDisengageTicks, or it already woke EMP-adapted, when a second EMP would not hold it. A pulled-back
    /// blocker stays on the inner line.
    /// </summary>
    private static void MechDisengage(CombatView view, CombatMemory m)
    {
        if (!view.Layout.TryGetValue(out var layout) || layout.Retreat.Count == 0 || m.EmpAdapted.Count == 0)
        {
            return;
        }

        var waking = new List<Cell>();
        foreach (var h in RankThreats(view))
        {
            if (h.Cell.TryGetValue(out var c) && IsMech(h) && EmpAdapted(m, h.Id) && h.StunTicks <= EmpDisengageTicks)
            {
                waking.Add(c);
            }
        }

        if (waking.Count == 0)
        {
            return;
        }

        var taken = new HashSet<Cell>();
        foreach (var r in m.Roles)
        {
            if (r.Retreat && r.Cell is { } held)
            {
                taken.Add(held);
            }
        }

        foreach (var r in m.Roles)
        {
            if (r.Duty != CombatDuty.Blocker || r.Retreat || r.Cell is not { } at || !waking.Any(c => Chebyshev(c, at) <= EmpNearBlocker))
            {
                continue;
            }

            if (RetreatCell(layout, r.Cell, taken) is { } cell)
            {
                r.Cell = cell;
                r.Retreat = true;
                r.Ground = null;
                r.Target = default;
            }
        }
    }
}
