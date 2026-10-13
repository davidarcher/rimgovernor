namespace RimGovernor.Brain.Policy;

/// <summary>A door order's mode.</summary>
public enum DoorMode
{
    /// <summary>Go's empty mode: no door order.</summary>
    [WireName("")]
    None = 0,
    Forbid,
    Allow,

    /// <summary>Holds a door open.</summary>
    HoldOpen,

    /// <summary>Shuts a door.</summary>
    Close,
}

/// <summary>One rescue_path cell: hostile line of fire on it, and a player door standing on it.</summary>
public sealed record RouteCell
{
    public Cell Cell { get; init; }

    public bool LineOfFire { get; init; }

    public bool Door { get; init; }
}

/// <summary>The rescue under way: the downed defender, the rescuer once ordered and the tick it went out, and the doors this rescue forbade.</summary>
public sealed record CombatRescue
{
    public PawnId Patient { get; init; }

    public PawnId Rescuer { get; init; }

    public Tick Ordered { get; init; }

    public EquatableArray<Cell> Doors { get; init; }

    /// <summary>Reports <paramref name="pawn"/> is the rescue's ordered rescuer: its role waits.</summary>
    public bool Carrying(PawnId pawn) => !Rescuer.IsEmpty && Rescuer == pawn;
}

public static partial class PolicyFunctions
{
    /// <summary>The Medicine level that makes a rescuer a doctor.</summary>
    internal const int RescueDoctorSkill = 6;

    /// <summary>The vanilla rescue job def the rescue order takes.</summary>
    internal const string RescueJob = "Rescue";

    /// <summary>
    /// The reaction table's rescue row. A downed stop for a defender opens a rescue. Each stop until it ends:
    /// pick a rescuer (a non-combatant doctor with a shield belt first, else the nearest non-blocker), ask the
    /// game for its route to the patient, and order the rescue only when no route cell is under hostile line
    /// of fire. A route through the fight forbids its doors next to a fire cell and waits. The rescue ends when
    /// the patient is up or dead, or the rescuer's job is no longer the rescue at a later stop, and its doors
    /// are allowed again. Returns the updated memory, the stop's rescue and door orders, or the geometry ask.
    /// </summary>
    internal static (CombatMemory Memory, List<CombatOrder> Orders, GeometryRequest? Request) RescueStep(
        CombatView view,
        GeometryReply geometry,
        StopEvent stop,
        CombatMemory m,
        IReadOnlySet<PawnId> orderable,
        IReadOnlyDictionary<PawnId, CombatPawnState> state)
    {
        if (m.Rescue is null)
        {
            if (stop.Kind != CombatStopKind.Downed || !IsDefender(view, stop.Pawn))
            {
                return (m, [], null);
            }
            m = m with { Rescue = new CombatRescue { Patient = stop.Pawn } };
        }
        var r = m.Rescue!;
        if (!state.TryGetValue(r.Patient, out var patient) || patient.Dead || !patient.Downed)
        {
            var (ended, endOrders) = EndRescue(m);
            return (ended, endOrders, null);
        }
        if (!r.Rescuer.IsEmpty)
        {
            var known = state.TryGetValue(r.Rescuer, out var s);
            if (!known || s!.Dead || s.Downed || !orderable.Contains(r.Rescuer))
            {
                r = r with { Rescuer = default };
                m = m with { Rescue = r };
            }
            else if (view.Tick > r.Ordered && s.Job.Length != 0 && s.Job != RescueJob)
            {
                var (ended, endOrders) = EndRescue(m);
                return (ended, endOrders, null);
            }
            else
            {
                return (m, [], null);
            }
        }
        var toKnown = patient.Cell.TryGetValue(out var to);
        var rescuer = PickRescuer(view, m, orderable, state, to, toKnown);
        if (rescuer.IsEmpty || !toKnown)
        {
            return (m, [], null);
        }
        var order = new CombatOrder { Pawn = rescuer, Kind = CombatOrderKind.Rescue, Target = r.Patient, Reason = CombatOrderReason.Rescue };
        var hostiles = RescueHostiles(view);
        if (hostiles.Count == 0)
        {
            var (started, startOrders) = StartRescue(m, order, view.Tick);
            return (started, startOrders, null);
        }
        if (geometry.Role != FormationRole.RescuePath)
        {
            if (geometry.Answered)
            {
                // The stop's one geometry read went to Formation.
                return (m, [], null);
            }
            return (m, [], new GeometryRequest { Propose = FormationRole.RescuePath, Pawn = rescuer, To = to, Hostiles = [.. hostiles] });
        }
        var route = geometry.Route;
        if (route.Count == 0 || !Adjacent(route[^1].Cell, to))
        {
            // No path, or one cut at the cap: no route is known safe.
            return (m, [], null);
        }
        if (!route.Any(c => c.LineOfFire))
        {
            var (started, startOrders) = StartRescue(m, order, view.Tick);
            return (started, startOrders, null);
        }
        var doors = new List<CombatOrder>();
        var forbidden = r.Doors.ToList();
        for (var i = 0; i < route.Count; i++)
        {
            var c = route[i];
            var fire = c.LineOfFire || (i + 1 < route.Count && route[i + 1].LineOfFire) || (i > 0 && route[i - 1].LineOfFire);
            if (c.Door && fire && !forbidden.Contains(c.Cell))
            {
                forbidden.Add(c.Cell);
                doors.Add(new CombatOrder { Kind = CombatOrderKind.Door, Cell = c.Cell, Door = DoorMode.Forbid, Reason = CombatOrderReason.Rescue });
            }
        }
        return (m with { Rescue = r with { Doors = EquatableArray.From(forbidden) } }, doors, null);
    }

    internal static (CombatMemory Memory, List<CombatOrder> Orders) StartRescue(CombatMemory m, CombatOrder order, Tick tick) =>
        (m with { Rescue = m.Rescue! with { Rescuer = order.Pawn, Ordered = tick } }, [order]);

    /// <summary>Closes the rescue and allows the doors it forbade.</summary>
    internal static (CombatMemory Memory, List<CombatOrder> Orders) EndRescue(CombatMemory m)
    {
        var orders = new List<CombatOrder>();
        foreach (var c in m.Rescue!.Doors)
        {
            orders.Add(new CombatOrder { Kind = CombatOrderKind.Door, Cell = c, Door = DoorMode.Allow, Reason = CombatOrderReason.Rescue });
        }
        return (m with { Rescue = null }, orders);
    }

    private readonly record struct RescueCandidate(PawnId Id, bool Shielded, int Medicine, long Distance);

    private static bool BetterRescuer(RescueCandidate a, RescueCandidate b)
    {
        if (a.Shielded != b.Shielded)
        {
            return a.Shielded;
        }
        if (a.Shielded && a.Medicine != b.Medicine)
        {
            return a.Medicine > b.Medicine;
        }
        if (a.Distance != b.Distance)
        {
            return a.Distance < b.Distance;
        }
        return a.Id < b.Id;
    }

    /// <summary>
    /// The rescue's pawn: an orderable, live defender other than the patient, not holding a choke as a
    /// blocker. A non-combatant doctor with a shield belt comes first, the best Medicine first; otherwise the
    /// nearest to the patient. Ties go by id.
    /// </summary>
    internal static PawnId PickRescuer(CombatView view, CombatMemory m, IReadOnlySet<PawnId> orderable, IReadOnlyDictionary<PawnId, CombatPawnState> state, Cell to, bool known)
    {
        var roles = new Dictionary<PawnId, CombatRole>();
        foreach (var r in m.Roles)
        {
            roles[r.Pawn] = r;
        }
        var live = view.Live();
        RescueCandidate? best = null;
        foreach (var id in view.Orderable)
        {
            var ok = state.TryGetValue(id, out var s);
            roles.TryGetValue(id, out var role);
            var duty = role?.Duty ?? CombatDuty.None;
            if (!orderable.Contains(id) || !live.GetValueOrDefault(id) || id == m.Rescue!.Patient || !ok || s!.Dead || s.Downed || duty == CombatDuty.Blocker)
            {
                continue;
            }
            var shielded = s.ShieldBelt && s.MedicalSkill >= RescueDoctorSkill && (s.Weapon.Length == 0 || (role?.Target ?? default).IsEmpty);
            var distance = 1L << 62;
            if (s.Cell.TryGetValue(out var at) && known)
            {
                long dx = at.X - to.X;
                long dz = at.Z - to.Z;
                distance = (dx * dx) + (dz * dz);
            }
            var c = new RescueCandidate(id, shielded, s.MedicalSkill, distance);
            if (best is null || BetterRescuer(c, best.Value))
            {
                best = c;
            }
        }
        return best?.Id ?? default;
    }

    /// <summary>The live hostiles the route is scored against, the top-scored first, at most the geometry cap.</summary>
    internal static List<PawnId> RescueHostiles(CombatView view)
    {
        var result = new List<PawnId>();
        foreach (var h in RankThreats(view))
        {
            if (result.Count < MaxGeometryHostiles)
            {
                result.Add(h.Id);
            }
        }
        return result;
    }

    internal static bool IsDefender(CombatView view, PawnId id) => !id.IsEmpty && view.Defenders.Any(d => d.Id == id);

    internal static bool Adjacent(Cell a, Cell b) => a == b || (Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Z - b.Z) <= 1);
}
