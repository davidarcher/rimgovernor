namespace RimGovernor.Brain.Policy;

/// <summary>One sheltering room's door and the room's floor cell directly behind it.</summary>
public readonly record struct WaitDoor(Cell Door, Cell Inside);

/// <summary>The census of one cell the wait hardening reads: the edifice standing on it and its stuff.</summary>
public sealed record WaitDoorCell
{
    public string Edifice { get; init; } = "";

    public string Stuff { get; init; } = "";

    public bool Walkable { get; init; }

    public bool Roofed { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Bounds a humanoid raid's wait: raiders give up and leave 26k-38k ticks after they arrive (sappers
    /// 33k-38k), so a raid still here past the window's end is not leaving and the fight re-forms. Kept: the
    /// vanilla window's end (docs/developers/contracts/kept-constants.md).
    /// </summary>
    internal const int RaidGiveUpTicks = 38000;

    // Extreme outdoor temperatures: past these a fight shelters indoors and lets hypothermia or heatstroke
    // wear the raiders down.
    internal const double ExtremeColdC = -20.0;
    internal const double ExtremeHeatC = 45.0;

    /// <summary>Wait hardening's stuff: a plasteel door holds far longer than a wooden one.</summary>
    public const string WaitDoorStuff = "Plasteel";

    /// <summary>A Door's plasteel stuff cost.</summary>
    internal const int PlasteelDoorCost = 25;

    /// <summary>
    /// The wait-it-out comparison: ours is the health fraction summed over the armed, eligible defenders;
    /// theirs is the live hostiles' body sizes. Unknown health or size counts 1.
    /// </summary>
    internal static bool Outmatched(CombatView view)
    {
        double ours = 0;
        double theirs = 0;
        foreach (var d in view.Defenders)
        {
            if (SquadDefenderEligible(d) && Positive(d.Armed))
            {
                if (!d.HealthFraction.TryGetValue(out var h))
                {
                    h = 1;
                }
                ours += h;
            }
        }
        var down = DownPawns(view);
        foreach (var t in view.Threats)
        {
            if (t.Building || Positive(t.Dead) || Positive(t.Downed) || down.GetValueOrDefault(new PawnId(t.Id)))
            {
                continue;
            }
            if (!t.BodySize.TryGetValue(out var size) || size <= 0)
            {
                size = 1;
            }
            theirs += size;
        }
        return ours < theirs;
    }

    /// <summary>Reports a known outdoor temperature at or past the cold or heat bound; unknown never shelters.</summary>
    internal static bool ExtremeWeather(CombatView view) =>
        view.OutdoorTemperatureC.TryGetValue(out var t) && (t <= ExtremeColdC || t >= ExtremeHeatC);

    /// <summary>
    /// Reports a fight whose live hostile pawns (at least one) are all known humanlike, with no pods arrival
    /// and no siege lord (those wait by their own tactics), and no complete hold-the-line layout: a killbox
    /// keeps fighting; only the squad fallback waits.
    /// </summary>
    internal static bool HumanoidRaid(CombatView view, CombatMemory m)
    {
        if (view.Layout.IsKnown)
        {
            return false;
        }
        if (view.Pods.IsKnown || m.Pods is not null || LiveBesiegers(view).Count > 0)
        {
            return false;
        }
        var down = DownPawns(view);
        var n = 0;
        foreach (var t in view.Threats)
        {
            if (t.Building || Positive(t.Dead) || Positive(t.Downed) || down.GetValueOrDefault(new PawnId(t.Id)))
            {
                continue;
            }
            // A shambler is humanlike but fights as a pack of its own.
            if (!Positive(t.Humanlike) || Anomalous(t))
            {
                return false;
            }
            n++;
        }
        return n > 0;
    }

    /// <summary>
    /// Runs before formation: it decides whether a manhunter fight or a humanoid raid waits this stop:
    /// outmatched, or in extreme outdoor cold or heat. A raid waits at most RaidGiveUpTicks from its first
    /// waiting stop. A fight that stops waiting drops its roles so it re-forms to fight, and allows the doors it forbade.
    /// </summary>
    internal static CombatMemory WaitTurn(CombatView view, CombatMemory m)
    {
        var raid = HumanoidRaid(view, m);
        var insects = Infestation(view);
        if (!ManhunterPack(view) && !insects && !raid)
        {
            return m;
        }
        // An outmatched infestation evacuates, a lit burn-out waits out its fire; insects under the mountain
        // do not feel the weather.
        var wait = Outmatched(view) || (!insects && ExtremeWeather(view)) || BurnWaiting(m);
        if (wait && !m.Wait)
        {
            m = m with { WaitSince = view.Tick };
        }
        if (raid && view.Tick.Value - m.WaitSince.Value >= RaidGiveUpTicks)
        {
            wait = false;
        }
        if (m.Wait && !wait)
        {
            var allow = new List<PodDoor>();
            foreach (var d in m.WaitDoors)
            {
                if (d.Mode == DoorMode.Forbid)
                {
                    allow.Add(new PodDoor { Cell = d.Cell, Mode = DoorMode.Allow });
                }
            }
            m = m with { Roles = default, WaitDoors = EquatableArray.From(allow) };
        }
        return m with { Wait = wait };
    }

    /// <summary>
    /// Applies the wait after formation: nobody engages or goes out (no kiter, potshot door, intercept, rush
    /// or lure), every role takes an inner-line cell when the layout has one (else holds where it stands), and
    /// every planned room door is closed and forbidden, so colony animals cannot open it and nobody paths out through it.
    /// </summary>
    internal static CombatMemory Shelter(CombatView view, CombatMemory m)
    {
        if (!m.Wait || m.Tactic == CombatTactic.Shelter)
        {
            // A fight with no armed defender already shelters.
            return m;
        }
        m = m with { Kiter = default, Leading = false, PotshotDoor = null, Intercept = false, Rushing = false, MechLure = false };
        var inner = new List<Cell>();
        if (view.Layout.TryGetValue(out var layout))
        {
            inner = layout.Retreat.ToList();
        }
        var at = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                at[p.Id] = c;
            }
        }
        var roles = m.Roles.ToList();
        for (var i = 0; i < roles.Count; i++)
        {
            var r = roles[i] with { Target = default, Duty = CombatDuty.None, Cell = null, Retreat = false };
            if (inner.Count > 0)
            {
                var k = NearestIndex(at.GetValueOrDefault(r.Pawn), inner);
                var c = inner[k];
                inner.RemoveAt(k);
                r = r with { Cell = c, Retreat = true };
            }
            roles[i] = r;
        }
        var want = new List<PodDoor>();
        var waitRooms = new List<WaitDoor>();
        foreach (var room in view.Rooms)
        {
            foreach (var d in room.Doors)
            {
                if (!want.Any(p => p.Cell == d))
                {
                    want.Add(new PodDoor { Cell = d, Mode = DoorMode.Close });
                    want.Add(new PodDoor { Cell = d, Mode = DoorMode.Forbid });
                    var inside = BehindDoor(room, d);
                    if (inside is { } found)
                    {
                        waitRooms.Add(new WaitDoor(d, found));
                    }
                }
            }
        }
        return m with
        {
            Roles = EquatableArray.From(roles),
            WaitRooms = EquatableArray.From(waitRooms),
            WaitDoors = EquatableArray.From(KeepSent(m.WaitDoors, want)),
        };
    }

    /// <summary>The room floor cell orthogonally next to <paramref name="door"/>; null when none is.</summary>
    internal static Cell? BehindDoor(CombatRoom room, Cell door)
    {
        foreach (var d in new[] { new Cell(0, 1), new Cell(0, -1), new Cell(1, 0), new Cell(-1, 0) })
        {
            var c = new Cell(door.X + d.X, door.Z + d.Z);
            if (room.Contains(c))
            {
                return c;
            }
        }
        return null;
    }

    /// <summary>
    /// The layout planner's builds for a waiting fight: each standing door of a sheltering room not already
    /// plasteel is rebuilt in plasteel while the stock holds 25 plasteel for it, and each door the census no
    /// longer shows (broken) gets a wall of <paramref name="wallStuff"/> on the open floor cell behind it. A
    /// cell the census does not carry is left alone. A door takes no rotation: the game orients it between its walls.
    /// </summary>
    public static List<Building> WaitHardening(CombatMemory m, IReadOnlyDictionary<Cell, WaitDoorCell> census, long plasteel, string door, string wall, string wallStuff)
    {
        var result = new List<Building>();
        if (!m.Wait)
        {
            return result;
        }
        foreach (var w in m.WaitRooms)
        {
            if (!census.TryGetValue(w.Door, out var at))
            {
                continue;
            }
            if (at.Edifice == door && at.Stuff != WaitDoorStuff && plasteel >= PlasteelDoorCost)
            {
                result.Add(new Building(door, w.Door, Rotation.North, WaitDoorStuff));
                plasteel -= PlasteelDoorCost;
            }
            else if (at.Edifice.Length == 0 && census.TryGetValue(w.Inside, out var inside) && inside.Edifice.Length == 0 && inside.Walkable)
            {
                result.Add(new Building(wall, w.Inside, Rotation.North, wallStuff));
            }
        }
        return result;
    }
}
