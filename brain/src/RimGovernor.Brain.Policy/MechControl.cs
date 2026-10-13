using DomainPawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Policy;

/// <summary>What a mech kind is for.</summary>
public enum MechRole
{
    [WireName("")]
    None = 0,
    Worker,
    Guard,
}

/// <summary>One controllable mech kind's catalog facts (bridge.BiotechCatalog).</summary>
public sealed record MechKind
{
    public string Name { get; init; } = "";

    /// <summary>The race's IsWorkMech: it does work; any other kind is a combat mech.</summary>
    public bool WorkMech { get; init; }

    public IReadOnlyList<WorkType> WorkTypes { get; init; } = [];
    public double BandwidthCost { get; init; }

    /// <summary>The kind's PawnKindDef combatPower.</summary>
    public double CombatPower { get; init; }

    /// <summary>Worker for a work mech, guard for a combat mech.</summary>
    public MechRole Role => WorkMech ? MechRole.Worker : MechRole.Guard;
}

/// <summary>The Biotech defs the planner reads: kinds and work mode names. The default value is a game without Biotech.</summary>
public sealed record MechCatalog
{
    public IReadOnlyDictionary<string, MechKind> Kinds { get; init; } = new Dictionary<string, MechKind>();

    /// <summary>
    /// The work modes the game names MechWorkModeDefOf.Work, .Escort and .Recharge (the catalog rows' role flags,
    /// each on exactly one row); empty without Biotech.
    /// </summary>
    public string Work { get; init; } = "";

    public string Escort { get; init; } = "";
    public string Recharge { get; init; } = "";
}

/// <summary>One colonist mechanitor. Go embeds <c>PawnMechanitor</c>; here it is composed with forwarding members.</summary>
public sealed record MechanitorInput
{
    public PawnId Id { get; init; }
    public PawnMechanitor Mechanitor { get; init; } = new();

    /// <summary>Where the mechanitor stands; unknown plans no guard orders.</summary>
    public Fact<Cell> Cell { get; init; }

    public Fact<int> UsedBandwidth => Mechanitor.UsedBandwidth;
    public Fact<int> TotalBandwidth => Mechanitor.TotalBandwidth;
    public Fact<int> GestationBandwidth => Mechanitor.GestationBandwidth;
    public Fact<int> ControlGroups => Mechanitor.ControlGroups;
    public IReadOnlyList<string> ControlledMechs => Mechanitor.ControlledMechs;
}

/// <summary>One colony mech with its Biotech row. Go embeds <c>PawnMech</c>; here it is composed with forwarding members.</summary>
public sealed record MechInput
{
    public PawnId Id { get; init; }
    public string Kind { get; init; } = "";
    public PawnMech Mech { get; init; } = new();
    public Fact<Cell> Cell { get; init; }
    public bool Downed { get; init; }
    public bool Drafted { get; init; }

    /// <summary>The pawn its current attack job targets, empty for none.</summary>
    public PawnId Target { get; init; }

    public string Overseer => Mech.Overseer;
    public Fact<string> WorkMode => Mech.WorkMode;
    public Fact<int> ControlGroup => Mech.ControlGroup;
    public Fact<double> Energy => Mech.Energy;
    public Fact<double> RechargeBelow => Mech.RechargeBelow;
    public Fact<double> RechargeAbove => Mech.RechargeAbove;
}

/// <summary>The colony's mechanitors and mechs as one read recorded them.</summary>
public sealed record MechFleet
{
    public IReadOnlyList<MechanitorInput> Mechanitors { get; init; } = [];
    public IReadOnlyList<MechInput> Mechs { get; init; } = [];
}

/// <summary>A hostile a guard may be ordered at.</summary>
public sealed record MechHostile
{
    public PawnId Id { get; init; }
    public Cell Cell { get; init; }
}

/// <summary>The combat writes for the guard mechs: the drafts, then the attack orders, in the existing combat batch's shapes.</summary>
public sealed record MechGuardPlan
{
    public IReadOnlyList<DomainPawnId> Drafts { get; init; } = [];
    public IReadOnlyList<CombatOrder> Orders { get; init; } = [];
}

/// <summary>One planned control group (Go <c>mechGroup</c>).</summary>
public sealed record MechGroup
{
    public int Index { get; init; }
    public string Mode { get; init; } = "";
    public IReadOnlyList<MechInput> Mechs { get; init; } = [];

    /// <summary>
    /// Whether the group already runs the mode: a mech now in the group has a known mode and every one that is
    /// has the wanted one. An empty group, or one with an unread mode, is written.
    /// </summary>
    public bool HoldsMode(IReadOnlyList<MechInput> all) => Holds(all, Mode);

    /// <summary>HoldsMode for an arbitrary mode.</summary>
    public bool Holds(IReadOnlyList<MechInput> all, string wanted)
    {
        var seen = false;
        foreach (var mech in all)
        {
            if (!mech.ControlGroup.TryGetValue(out var group) || group != Index)
            {
                continue;
            }
            if (!mech.WorkMode.TryGetValue(out var mode) || mode != wanted)
            {
                return false;
            }
            seen = true;
        }
        return seen;
    }
}

/// <summary>
/// Ports <c>mech_control.go</c>. A mechanitor's mechs are split by role into control groups and each group is
/// given the work mode its role needs. Workers (a race that does work, RaceProperties.IsWorkMech) and guards (a
/// combat mech) never share a group while the overseer has two or more. Modes are Work, Escort, Recharge
/// (MechRechargeMode) and SelfShutdown, which is not chosen here. Bandwidth is spent only by acquiring mechs
/// (gestation); control never frees it. Colonist need decides what the next free bandwidth buys (MechRoleNext).
/// With one control group the group mode follows threat over work: Escort while a hostile is on the map or when
/// the group holds only guards, Work otherwise.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>How far from its overseer a mech can be ordered (Mechanitor wiki: 25 tiles; the game's own check, MechanitorUtility.InMechanitorCommandRange, stays authoritative).</summary>
    public const int MechCommandRange = 25;

    /// <summary>
    /// The settings that bring each mechanitor's mechs into role groups with the right modes: first every move,
    /// then every group mode, so a mode lands on a group after its mechs are in it. hostile is a hostile on the
    /// map; chargerReady is a charger a mech can use now (MechRechargeMode). An unknown group count, an unknown
    /// mech group or a mech whose overseer is not an input mechanitor is skipped; a mech kind or mode the catalog
    /// lacks throws.
    /// </summary>
    public static List<PawnSettings> PlanMechControl(MechCatalog catalog, IReadOnlyList<MechanitorInput> mechanitors, IReadOnlyList<MechInput> mechs, bool hostile, bool chargerReady)
    {
        foreach (var (role, mode) in new[] { ("work", catalog.Work), ("escort", catalog.Escort), ("recharge", catalog.Recharge) })
        {
            if (mode == "")
            {
                throw new DomainException($"mech {role} work mode is not in the catalog");
            }
        }
        var moves = new List<PawnSettings>();
        var modes = new List<PawnSettings>();
        foreach (var m in SortedMechanitors(mechanitors))
        {
            if (!m.ControlGroups.TryGetValue(out var groups) || groups < 1)
            {
                continue;
            }
            var workers = new List<MechInput>();
            var guards = new List<MechInput>();
            foreach (var mech in mechs)
            {
                if (mech.Overseer != m.Id.Value)
                {
                    continue;
                }
                if (!catalog.Kinds.TryGetValue(mech.Kind, out var kind))
                {
                    throw new DomainException($"mech {mech.Id} kind {mech.Kind} is not in the catalog");
                }
                if (!mech.ControlGroup.IsKnown)
                {
                    continue;
                }
                if (kind.Role == MechRole.Worker)
                {
                    workers.Add(mech);
                }
                else
                {
                    guards.Add(mech);
                }
            }
            SortMechs(workers);
            SortMechs(guards);
            var assigned = new List<MechGroup>();
            if (groups == 1)
            {
                var mode = catalog.Work;
                if (hostile || workers.Count == 0)
                {
                    mode = catalog.Escort;
                }
                assigned.Add(new MechGroup { Index = 0, Mode = mode, Mechs = [.. workers, .. guards] });
            }
            else
            {
                var workerGroup = BusiestGroup(workers, -1, groups);
                var guardGroup = BusiestGroup(guards, workerGroup, groups);
                if (workers.Count > 0)
                {
                    assigned.Add(new MechGroup { Index = workerGroup, Mode = catalog.Work, Mechs = workers });
                }
                if (guards.Count > 0)
                {
                    assigned.Add(new MechGroup { Index = guardGroup, Mode = catalog.Escort, Mechs = guards });
                }
            }
            foreach (var planned in assigned)
            {
                foreach (var mech in planned.Mechs)
                {
                    if (mech.ControlGroup.TryGetValue(out var current) && current == planned.Index)
                    {
                        continue;
                    }
                    moves.Add(PawnSettings.NewMechControlGroupSetting(new DomainPawnId(mech.Id.Value), planned.Index));
                }
                var g = planned with { Mode = MechRechargeMode(catalog, planned, mechs, chargerReady) };
                if (g.HoldsMode(mechs))
                {
                    continue;
                }
                modes.Add(PawnSettings.NewMechWorkModeSetting(new DomainPawnId(g.Mechs[0].Id.Value), g.Mode));
            }
        }
        return [.. moves, .. modes];
    }

    /// <summary>The group index holding most of the mechs, lowest on a tie; with none, the lowest index that is not skip.</summary>
    private static int BusiestGroup(IReadOnlyList<MechInput> mechs, int skip, int groups)
    {
        var count = new Dictionary<int, int>();
        foreach (var mech in mechs)
        {
            if (mech.ControlGroup.TryGetValue(out var g) && g != skip && g < groups)
            {
                count[g] = count.GetValueOrDefault(g) + 1;
            }
        }
        var best = -1;
        for (var g = 0; g < groups; g++)
        {
            if (g != skip && (best < 0 || count.GetValueOrDefault(g) > count.GetValueOrDefault(best)))
            {
                best = g;
            }
        }
        return best;
    }

    private static List<MechanitorInput> SortedMechanitors(IReadOnlyList<MechanitorInput> input) => [.. input.OrderBy(m => m.Id)];

    private static void SortMechs(List<MechInput> input) => input.Sort((a, b) => a.Id.CompareTo(b.Id));

    /// <summary>
    /// The role the mechanitor's next free bandwidth should buy, by colonist need first: Worker while a work type
    /// has fewer colonist owners than demand and none of the mechanitor's work mechs covers it, Guard otherwise.
    /// Ok is false with no free bandwidth or an unread budget.
    /// </summary>
    public static (MechRole Role, bool Ok) MechRoleNext(MechCatalog catalog, MechanitorInput m, IReadOnlyList<MechInput> mechs, IReadOnlyList<WorkCoverage> coverage)
    {
        var uk = m.UsedBandwidth.TryGetValue(out var used);
        var tk = m.TotalBandwidth.TryGetValue(out var total);
        if (!uk || !tk || total - used <= 0)
        {
            return (MechRole.None, false);
        }
        var shortWork = ShortUncoveredWork(catalog, m, mechs, coverage);
        foreach (var work in shortWork)
        {
            foreach (var kind in catalog.Kinds.Values)
            {
                if (kind.WorkMech && kind.WorkTypes.Contains(work))
                {
                    return (MechRole.Worker, true);
                }
            }
        }
        return (MechRole.Guard, true);
    }

    /// <summary>The work types with fewer colonist owners than demand that none of the mechanitor's work mechs covers.</summary>
    private static HashSet<WorkType> ShortUncoveredWork(MechCatalog catalog, MechanitorInput m, IReadOnlyList<MechInput> mechs, IReadOnlyList<WorkCoverage> coverage)
    {
        var covered = new HashSet<WorkType>();
        foreach (var mech in mechs)
        {
            if (mech.Overseer != m.Id.Value)
            {
                continue;
            }
            if (!catalog.Kinds.TryGetValue(mech.Kind, out var kind))
            {
                throw new DomainException($"mech {mech.Id} kind {mech.Kind} is not in the catalog");
            }
            foreach (var w in kind.WorkTypes)
            {
                covered.Add(w);
            }
        }
        var shortWork = new HashSet<WorkType>();
        foreach (var c in coverage)
        {
            if (c.Owners < c.Demand && !covered.Contains(c.Work))
            {
                shortWork.Add(c.Work);
            }
        }
        return shortWork;
    }

    /// <summary>
    /// Orders every standing guard mech at the hostile nearest to it among those within MechCommandRange of its
    /// overseer, drafting it first when it is not. A guard already attacking that hostile, a downed guard, a guard
    /// whose overseer or position is unread or is no input mechanitor, and a hostile out of the overseer's range
    /// are left alone.
    /// </summary>
    public static MechGuardPlan PlanMechGuards(MechCatalog catalog, IReadOnlyList<MechanitorInput> mechanitors, IReadOnlyList<MechInput> mechs, IReadOnlyList<MechHostile> hostiles)
    {
        var drafts = new List<DomainPawnId>();
        var orders = new List<CombatOrder>();
        var overseers = new Dictionary<string, Cell>(StringComparer.Ordinal);
        foreach (var m in mechanitors)
        {
            if (m.Cell.TryGetValue(out var cell))
            {
                overseers[m.Id.Value] = cell;
            }
        }
        var ordered = mechs.ToList();
        SortMechs(ordered);
        foreach (var mech in ordered)
        {
            if (!catalog.Kinds.TryGetValue(mech.Kind, out var kind))
            {
                throw new DomainException($"mech {mech.Id} kind {mech.Kind} is not in the catalog");
            }
            var hok = overseers.TryGetValue(mech.Overseer, out var home);
            var aok = mech.Cell.TryGetValue(out var at);
            if (kind.Role != MechRole.Guard || mech.Downed || !hok || !aok)
            {
                continue;
            }
            MechHostile? best = null;
            foreach (var h in hostiles)
            {
                if (CellDistSq(home, h.Cell) > (long)MechCommandRange * MechCommandRange)
                {
                    continue;
                }
                if (best is null || CellDistSq(at, h.Cell) < CellDistSq(at, best.Cell) || (CellDistSq(at, h.Cell) == CellDistSq(at, best.Cell) && h.Id < best.Id))
                {
                    best = h;
                }
            }
            if (best is null || mech.Target == best.Id)
            {
                continue;
            }
            if (!mech.Drafted)
            {
                drafts.Add(new DomainPawnId(mech.Id.Value));
            }
            orders.Add(new CombatOrder { Pawn = new PawnId(mech.Id.Value), Kind = CombatOrderKind.Attack, Target = new PawnId(best.Id.Value), Reason = CombatOrderReason.Formation });
        }
        return new MechGuardPlan { Drafts = drafts, Orders = orders };
    }

    private static long CellDistSq(Cell a, Cell b)
    {
        long dx = a.X - b.X;
        long dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }
}
