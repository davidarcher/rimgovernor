using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>A combat simulator. A Sim is ready for Reset once constructed and is not safe for concurrent use.</summary>
public sealed partial class Sim
{
    private readonly List<PawnState> _pawnStates = [];
    private int _step;
    private bool _applied;
    private int _nextId;
    private double _ff;
    private double _wxAcc;

    // scratch reused across calls
    private readonly List<Cell> _leanSource = [];
    private readonly List<Cell> _leanDest = [];
    private readonly Search _search = new();
    private readonly List<CoverGiver> _givers = [];

    internal Grid G { get; } = new();

    internal int TickCount { get; private set; }

    internal double Weather { get; private set; }

    internal List<SimPawn> Pawns { get; } = [];

    /// <summary>Runs in the pather tick that ends pawn i's walk (the adapter's Wait_Combat auto-attack).</summary>
    internal Action<int>? OnArrive { get; set; }

    /// <summary>Who stands where; pawns share cells (A35).</summary>
    internal int[] Head { get; private set; } = [];

    internal Streams RandomStreams { get; private set; } = null!;

    internal List<SimEvent> Events { get; } = [];

    /// <summary>The current game tick (0 after Reset).</summary>
    public int Tick => TickCount;

    /// <summary>The random streams of the current run.</summary>
    public Streams StreamsOfRun => RandomStreams;

    /// <summary>
    /// Builds the world from <paramref name="sc"/> and seeds the random streams from <paramref name="seed"/>.
    /// Throws <see cref="UnsupportedScenarioException"/> for a scenario it cannot represent.
    /// </summary>
    public SimState Reset(Scenario sc, ulong seed)
    {
        Load(sc);
        TickCount = 0;
        RandomStreams = new Streams(
            new GoRandom(seed, Streams.PhysicalStream),
            new GoRandom(seed, Streams.EnemyStream),
            new GoRandom(seed, Streams.PhaseStream),
            new GoRandom(seed, Streams.ColonistStream));
        Events.Clear();
        _applied = false;
        _nextId = ThingIds.FirstThingId(RandomStreams.Phase);
        _pending.Clear();
        _ledger.Clear();
        return Snapshot();
    }

    /// <summary>
    /// Applies orders at the current tick without advancing it; events they raise join the next Step's. An
    /// order naming no pawn throws and applies nothing after it. A caller that derives orders from the pawns'
    /// state (the fire-at-will holders' Autonomy) applies the orders already decided first, so it reads a pawn
    /// that has just been told to walk as walking.
    /// </summary>
    public void Apply(IReadOnlyList<Order> orders)
    {
        if (!_applied)
        {
            Events.Clear();
            _applied = true;
        }
        foreach (var o in orders)
        {
            ApplyOrder(o);
        }
    }

    /// <summary>
    /// Applies orders, advances StepTicks ticks and returns the new state and the events of those ticks in
    /// order. An order naming no pawn throws and applies nothing after it.
    /// </summary>
    public (SimState State, IReadOnlyList<SimEvent> Events) Step(IReadOnlyList<Order> orders)
    {
        try
        {
            Apply(orders);
        }
        finally
        {
            _applied = false;
        }
        for (var n = 0; n < _step; n++)
        {
            TickCount++;
            for (var i = 0; i < Pawns.Count; i++)
            {
                PawnTick(Pawns[i], i);
                if (Pawns[i].Arrived)
                {
                    Pawns[i].Arrived = false;
                    OnArrive?.Invoke(i);
                }
                CombatTick(i);
                HealthTick(i);
            }
            LandProjectiles();
        }
        return (Snapshot(), Events);
    }

    internal SimState Snapshot()
    {
        _pawnStates.Clear();
        foreach (var p in Pawns)
        {
            _pawnStates.Add(new PawnState(p.Id, p.Pos, p.Moving, p.Dest, p.Damage, p.Downed, p.Dead));
        }
        return new SimState(TickCount, _pawnStates);
    }

    internal void Emit(SimEvent e) => Events.Add(e with { Tick = TickCount });

    internal int PawnIndex(PawnId id)
    {
        for (var i = 0; i < Pawns.Count; i++)
        {
            if (Pawns[i].Id == id)
            {
                return i;
            }
        }
        return -1;
    }

    private void Load(Scenario sc)
    {
        var n = sc.Width * sc.Height;
        if (sc.Width <= 0 || sc.Height <= 0)
        {
            throw new UnsupportedScenarioException($"map size {sc.Width}x{sc.Height}");
        }
        if (sc.StepTicks <= 0)
        {
            throw new UnsupportedScenarioException($"StepTicks {sc.StepTicks} must be positive");
        }
        if (sc.Terrain.Count == 0 || sc.Terrain.Count > 255 || sc.Things.Count > 255)
        {
            throw new UnsupportedScenarioException($"palette sizes terrain {sc.Terrain.Count} things {sc.Things.Count}");
        }
        if ((sc.TerrainIds is not null && sc.TerrainIds.Count != n) ||
            (sc.ThingIds is not null && sc.ThingIds.Count != n) ||
            (sc.Roofed is not null && sc.Roofed.Count != n))
        {
            throw new UnsupportedScenarioException($"a cell grid does not match the {n}-cell map");
        }
        if (!sc.WeatherMoveSpeed.TryGetValue(out var weather) || !(weather > 0))
        {
            throw new UnsupportedScenarioException("weather move speed multiplier unknown or not positive");
        }
        var g = G;
        g.W = sc.Width;
        g.H = sc.Height;
        g.LoadPalettes(sc.Terrain, sc.Things);
        g.TerrainI = Fill(sc.TerrainIds, n);
        g.ThingI = Fill(sc.ThingIds, n);
        for (var i = 0; i < n; i++)
        {
            if (g.TerrainI[i] >= g.Terrain.Count || g.ThingI[i] >= g.Things.Count)
            {
                throw new UnsupportedScenarioException($"cell {i} names a palette entry that does not exist");
            }
        }
        g.Roofed = new bool[n];
        if (sc.Roofed is not null)
        {
            for (var i = 0; i < n; i++)
            {
                g.Roofed[i] = sc.Roofed[i];
            }
        }
        g.DoorOpen = new bool[n];
        foreach (var c in sc.OpenDoors)
        {
            if (!g.InBounds(c) || !g.Thing(c).Door)
            {
                throw new UnsupportedScenarioException($"open door at {c} is not a door cell");
            }
            g.DoorOpen[g.Idx(c)] = true;
        }
        Weather = weather;
        _step = sc.StepTicks;
        Head = new int[n];
        Pawns.Clear();
        for (var i = 0; i < sc.Pawns.Count; i++)
        {
            var ps = sc.Pawns[i];
            if (!ps.MoveSpeed.TryGetValue(out var speed) || speed < 0)
            {
                throw new UnsupportedScenarioException($"pawn \"{ps.Id}\" move speed unknown or negative");
            }
            if (!g.Standable(ps.Pos))
            {
                throw new UnsupportedScenarioException($"pawn \"{ps.Id}\" starts on {ps.Pos}, which is not standable");
            }
            if (PawnIndex(ps.Id) >= 0)
            {
                throw new UnsupportedScenarioException($"duplicate pawn id \"{ps.Id}\"");
            }
            var np = new SimPawn { Id = ps.Id, Pos = ps.Pos, Speed = speed, Dest = ps.Pos, Side = ps.Side };
            if (ps.Combat is not null)
            {
                np.LoadCombat(ps);
            }
            Pawns.Add(np);
            Enter(g.Idx(ps.Pos), i);
        }
        LoadCombatWorld(sc);
    }

    private static byte[] Fill(IReadOnlyList<byte>? src, int n)
    {
        var dst = new byte[n];
        if (src is not null)
        {
            for (var i = 0; i < n; i++)
            {
                dst[i] = src[i];
            }
        }
        return dst;
    }
}
