namespace RimGovernor.Brain.Policy;

/// <summary>An unroofed player mortar the fight may crew, with its verb's range in cells.</summary>
public sealed record CombatMortar
{
    public string Id { get; init; } = "";

    public Cell Cell { get; init; }

    public double MinRange { get; init; }

    public double MaxRange { get; init; }

    /// <summary>The loaded shell's def, empty when unloaded.</summary>
    public string Loaded { get; init; } = "";
}

/// <summary>What a mortar shell does when it lands, the kinds the counter-battery fires: blast, burn or stun.</summary>
public enum ShellKind
{
    None = 0,
    HE = 1,
    Incendiary,
    EMP,
}

/// <summary>
/// The shell defs of the load that do each kind of damage, classified from their projectile's damage def
/// rows. A kind the load has no shell for, or states ambiguously, is empty: the counter-battery fires what
/// the mortar holds instead and the armory stocks none.
/// </summary>
public sealed record MortarShells
{
    public string HE { get; init; } = "";

    public string Incendiary { get; init; } = "";

    public string EMP { get; init; } = "";

    /// <summary>The shell def of <paramref name="kind"/>, empty when the load has none.</summary>
    public string Def(ShellKind kind) => kind switch
    {
        ShellKind.HE => HE,
        ShellKind.Incendiary => Incendiary,
        ShellKind.EMP => EMP,
        _ => "",
    };
}

/// <summary>
/// A standing hostile building in the census: a crashed ship part, a mech-cluster piece, a siege or mech
/// mortar, a hive. Def is its ThingDef; Cell one occupied cell; Mortar the native def fact, a turret whose
/// verb fires mortar shells.
/// </summary>
public sealed record HostileStructure
{
    public PawnId Id { get; init; }

    public string Def { get; init; } = "";

    public Cell Cell { get; init; }

    public bool Mortar { get; init; }

    internal bool Hive => Def == "Hive";
}

/// <summary>A mortar stood down: its aim is gone, so its forced target is cleared and Crew, its last crew, is released to its role.</summary>
public readonly record struct MortarStand(Cell Cell, PawnId Crew);

public static partial class PolicyFunctions
{
    /// <summary>How far, in cells, besiegers count into one camp clump around a besieger the mortar aims at.</summary>
    internal const double CampClumpRadius = 5.0;

    /// <summary>The friendly danger radius: a target with a colonist this close has arrived, and a mortar's scatter would land on our own.</summary>
    public const double MortarSafeRadius = 10.0;

    /// <summary>
    /// Drops a melee target on a hostile structure other than a hive: a crashed ship part and its cluster
    /// buildings are destroyed from range. Gunners keep theirs.
    /// </summary>
    internal static CombatMemory FromRange(CombatView view, CombatMemory m)
    {
        var far = new Dictionary<PawnId, bool>();
        foreach (var s in view.Structures)
        {
            far[s.Id] = !s.Hive;
        }
        var roles = m.Roles.ToList();
        for (var i = 0; i < roles.Count; i++)
        {
            if (!roles[i].Ranged && far.GetValueOrDefault(roles[i].Target))
            {
                roles[i] = roles[i] with { Target = default };
            }
        }
        return m with { Roles = EquatableArray.From(roles) };
    }

    /// <summary>
    /// Crews each colony mortar with a target in its range: EMP on the nearest enemy mortar, else HE on the
    /// siege camp's densest clump (incendiary from a second mortar on the same camp), else HE on the nearest
    /// approaching centipede, else HE on the nearest other non-hive structure. Returns the updated memory and
    /// the mortars crewed at the last stop that now have nothing to aim at, each with its last crew.
    /// </summary>
    internal static (CombatMemory Memory, IReadOnlyList<MortarStand> Stood) CounterBattery(CombatView view, IReadOnlyList<CombatRole> prev, CombatMemory memory)
    {
        var crewed = new Dictionary<Cell, PawnId>();
        foreach (var r in prev)
        {
            // The last stop's crews, which a re-formation this stop has already dropped from memory.
            if (r.Mortar is { } mortarCell)
            {
                crewed[mortarCell] = r.Pawn;
            }
        }
        var roles = memory.Roles.ToList();
        for (var i = 0; i < roles.Count; i++)
        {
            if (roles[i].Mortar is { } mortarCell)
            {
                crewed[mortarCell] = roles[i].Pawn;
                roles[i] = roles[i] with { Mortar = null, Aim = null };
            }
        }
        var orderable = new HashSet<PawnId>(view.Orderable);
        var live = view.Live();
        var cells = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                cells[p.Id] = c;
            }
        }
        var busy = new HashSet<PawnId>();
        var mortars = view.Mortars.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        var shelled = new HashSet<Cell>(); // camp aims HE already falls on
        var stood = new List<MortarStand>();
        foreach (var mortar in mortars)
        {
            var (aim, kind, ok) = MortarAim(view, mortar);
            if (!ok)
            {
                if (crewed.TryGetValue(mortar.Cell, out var was))
                {
                    stood.Add(new MortarStand(mortar.Cell, was));
                }
                continue;
            }
            if (kind == ShellKind.HE && CampAim(view, aim) && shelled.Contains(aim))
            {
                kind = ShellKind.Incendiary;
            }
            bool Free(PawnId id) => orderable.Contains(id) && live.GetValueOrDefault(id) && !busy.Contains(id) && !(memory.Rescue?.Carrying(id) ?? false);
            var crew = crewed.GetValueOrDefault(mortar.Cell);
            if (!Free(crew))
            {
                crew = default;
                var best = double.MaxValue;
                foreach (var id in view.Orderable)
                {
                    if (!Free(id) || !cells.TryGetValue(id, out var c))
                    {
                        continue;
                    }
                    var d = Dist(c, mortar.Cell);
                    if (d < best)
                    {
                        crew = id;
                        best = d;
                    }
                }
            }
            if (crew.IsEmpty)
            {
                continue;
            }
            busy.Add(crew);
            if (kind == ShellKind.HE && CampAim(view, aim))
            {
                shelled.Add(aim);
            }
            // A kind the load has no shell for, or one refused for want of it, fires what is loaded.
            var shell = view.Shells.Def(kind);
            if (shell.Length == 0 || memory.NoShells.Contains(shell))
            {
                shell = mortar.Loaded;
            }
            var index = roles.FindIndex(r => r.Pawn == crew);
            if (index < 0)
            {
                roles.Add(new CombatRole { Pawn = crew });
                index = roles.Count - 1;
            }
            roles[index] = roles[index] with { Mortar = mortar.Cell, Aim = aim, Shell = shell };
        }
        return (memory with { Roles = EquatableArray.From(SortRoles(roles)) }, stood);
    }

    /// <summary>
    /// The orders of the mortars stood down: a pawnless mortar_fire with no target clears each forced target,
    /// and a crew still manning it with no other order this stop is stopped, ending ManTurret.
    /// </summary>
    internal static List<CombatOrder> StandDown(IReadOnlyList<MortarStand> stood, List<CombatOrder> orders, IReadOnlySet<PawnId> orderable, IReadOnlyDictionary<PawnId, CombatPawnState> state)
    {
        foreach (var s in stood)
        {
            orders.Add(new CombatOrder { Kind = CombatOrderKind.MortarFire, Cell = s.Cell, Clear = true, Reason = CombatOrderReason.CounterBattery });
            var ordered = orders.Any(o => o.Pawn == s.Crew);
            if (orderable.Contains(s.Crew) && !ordered && state.TryGetValue(s.Crew, out var st) && st.Job == "ManTurret")
            {
                orders.Add(new CombatOrder { Pawn = s.Crew, Kind = CombatOrderKind.Stop, Reason = CombatOrderReason.CounterBattery });
            }
        }
        return orders;
    }

    /// <summary>
    /// The cell a mortar fires at and the shell: EMP on the nearest enemy mortar in its range, else HE on a
    /// psychic ritual's caster, else HE on the camp clump, else HE on the nearest centipede, else HE on the
    /// nearest other non-hive structure.
    /// </summary>
    internal static (Cell Aim, ShellKind Kind, bool Ok) MortarAim(CombatView view, CombatMortar mortar)
    {
        bool InRange(Cell c)
        {
            var d = Dist(c, mortar.Cell);
            return d >= mortar.MinRange && d <= mortar.MaxRange;
        }
        (Cell, bool) Nearest(IEnumerable<Cell> cells)
        {
            Cell best = default;
            var found = false;
            foreach (var c in cells)
            {
                if (InRange(c) && (!found || Dist(c, mortar.Cell) < Dist(best, mortar.Cell)))
                {
                    best = c;
                    found = true;
                }
            }
            return (best, found);
        }
        var enemy = new List<Cell>();
        var other = new List<Cell>();
        foreach (var s in view.Structures)
        {
            if (s.Mortar)
            {
                enemy.Add(s.Cell);
            }
            else if (!s.Hive)
            {
                other.Add(s.Cell);
            }
        }
        var (cell, ok) = Nearest(enemy);
        if (ok)
        {
            return (cell, ShellKind.EMP, true);
        }
        (cell, ok) = Nearest(RitualCasterCells(view));
        if (ok)
        {
            return (cell, ShellKind.HE, true);
        }
        (cell, ok) = CampClump(view, InRange);
        if (ok)
        {
            return (cell, MechAt(view, cell) ? ShellKind.EMP : ShellKind.HE, true);
        }
        (cell, ok) = Nearest(ApproachingCentipedes(view));
        if (ok)
        {
            return (cell, ShellKind.HE, true);
        }
        (cell, ok) = Nearest(other);
        if (ok)
        {
            return (cell, ShellKind.HE, true);
        }
        return (default, ShellKind.None, false);
    }

    /// <summary>
    /// The cells of the live besiegers camped at the siege: a siege still travelling in is a moving raid the
    /// mortar leaves alone. A besieger within MortarSafeRadius of a colonist is never aimed at.
    /// </summary>
    internal static List<Cell> BesiegerCells(CombatView view)
    {
        var besiegers = LiveBesiegers(view);
        var ours = ColonistCells(view);
        var result = new List<Cell>();
        foreach (var p in view.Pawns)
        {
            if (p.Cell.TryGetValue(out var c) && besiegers.TryGetValue(p.Id, out var toil) && toil == SiegeCampToil && !NearAny(ours, c))
            {
                result.Add(c);
            }
        }
        return result;
    }

    /// <summary>The besieger cell in range with the most besiegers within CampClumpRadius of it (first in id order on a tie).</summary>
    internal static (Cell Cell, bool Ok) CampClump(CombatView view, Func<Cell, bool> inRange)
    {
        var cells = BesiegerCells(view);
        Cell best = default;
        var most = 0;
        foreach (var c in cells)
        {
            if (!inRange(c))
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

    /// <summary>Reports an aim on a besieger's cell.</summary>
    internal static bool CampAim(CombatView view, Cell aim) => BesiegerCells(view).Contains(aim);

    /// <summary>The live centipedes' cells: shelled while they walk in, before they reach the line inside the mortar's minimum range.</summary>
    internal static List<Cell> ApproachingCentipedes(CombatView view)
    {
        var hostile = new Dictionary<PawnId, bool>();
        foreach (var t in view.Threats)
        {
            hostile[t.Id] = !t.Building && !Positive(t.Dead) && !Positive(t.Downed);
        }
        var ours = ColonistCells(view);
        var result = new List<Cell>();
        foreach (var p in view.Pawns)
        {
            if (!p.Cell.TryGetValue(out var c) || !hostile.GetValueOrDefault(p.Id) || p.Dead || p.Downed || !p.Kind.StartsWith("Mech_Centipede", StringComparison.Ordinal))
            {
                continue;
            }
            if (!NearAny(ours, c))
            {
                result.Add(c);
            }
        }
        return result;
    }

    /// <summary>The defenders' known cells.</summary>
    internal static List<Cell> ColonistCells(CombatView view)
    {
        var ours = new List<Cell>();
        foreach (var d in view.Defenders)
        {
            foreach (var p in view.Pawns)
            {
                if (p.Cell.TryGetValue(out var c) && p.Id == d.Id)
                {
                    ours.Add(c);
                }
            }
        }
        return ours;
    }

    /// <summary>Reports a cell within MortarSafeRadius of one of ours.</summary>
    internal static bool NearAny(IReadOnlyList<Cell> ours, Cell c) => ours.Any(o => Dist(o, c) <= MortarSafeRadius);

    /// <summary>Reports a mechanoid standing on <paramref name="c"/>: EMP, not HE, on a camp of mechs.</summary>
    internal static bool MechAt(CombatView view, Cell c) => view.Pawns.Any(p => p.Cell.TryGetValue(out var at) && at == c && p.Mech);

    internal static double Dist(Cell a, Cell b) => Math.Sqrt(((double)(a.X - b.X) * (a.X - b.X)) + ((double)(a.Z - b.Z) * (a.Z - b.Z)));
}
