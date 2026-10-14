using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.Combat;
using RimGovernor.Brain.Simulation.Combat.Import;

// The hand-ported sim, fed the same Spec. Its catalog is built over the live game's Defs, so the importer reads the
// very values the real engine uses.
internal sealed class SimSide
{
    private readonly DefinitionCatalog _catalog;
    private readonly IStatSource _stats;

    internal SimSide()
    {
        var defs = new List<Verse.Def>();
        var seen = new HashSet<Verse.Def>(ReferenceEqualityComparer.Instance);
        foreach (var root in Verse.GenDefDatabase.AllDefTypesWithDatabases().Where(t => !t.IsAbstract))
        {
            foreach (var d in Verse.GenDefDatabase.GetAllDefsInDatabaseForDef(root))
            {
                if (seen.Add(d))
                {
                    defs.Add(d);
                }
            }
        }

        _stats = new RimGovernor.RealCombat.Native.ContextStats(RimGovernor.RealCombat.Native.InlineStatService.Instance);
        _catalog = new DefinitionCatalog(defs, _stats, new CatalogDerived("Silver", "Wort", 10f));
    }

    internal RimWorld.Difficulty Difficulty => Verse.Find.Storyteller.difficulty;

    internal Agg Run(Spec spec, IReadOnlyList<int> seeds, int maxTicks)
    {
        var imported = FightImporter.Import(_catalog, _stats, spec);
        foreach (var u in imported.Unmodeled)
        {
            Console.Error.WriteLine("  sim unmodeled: " + u);
        }

        var sideOf = spec.Pawns.ToDictionary(p => p.Id.Value, p => p.Side);
        var agg = new Agg();
        foreach (var seed in seeds)
        {
            var sim = new Sim();
            var state = sim.Reset(imported.Fight.Scenario, (ulong)seed);
            var target = new Dictionary<string, string>();
            var shots = new Dictionary<string, int>();
            var hits = new Dictionary<string, int>();
            var tick = 0;
            var winner = "";
            while (true)
            {
                var orders = Retarget(state, sideOf, target);
                if (orders.Count > 0)
                {
                    sim.Apply(orders);
                }

                var (st, events) = sim.Step([]);
                tick = st.Tick;
                state = st;
                foreach (var e in events)
                {
                    if (e.Kind == EventKind.Shot)
                    {
                        var side = sideOf[e.Pawn.Value];
                        shots[side] = shots.GetValueOrDefault(side) + 1;
                    }
                    else if (e.Kind == EventKind.Hit)
                    {
                        var side = sideOf[e.Target.Value];
                        hits[side] = hits.GetValueOrDefault(side) + 1;
                    }
                }

                var colonists = st.Pawns.Any(p => sideOf[p.Id.Value] == "colonist" && !p.Downed && !p.Dead);
                var hostiles = st.Pawns.Any(p => sideOf[p.Id.Value] == "hostile" && !p.Downed && !p.Dead);
                if (!colonists || !hostiles)
                {
                    winner = colonists && !hostiles ? "colonist" : hostiles && !colonists ? "hostile" : "";
                    break;
                }

                if (tick >= maxTicks)
                {
                    break;
                }
            }

            agg.Add(winner, tick, shots, hits);
        }

        return agg;
    }

    // Every pawn shoots the nearest opponent still standing until it is out, as the real host does.
    private static List<Order> Retarget(SimState state, Dictionary<string, string> sideOf, Dictionary<string, string> target)
    {
        var orders = new List<Order>();
        foreach (var me in state.Pawns)
        {
            if (me.Downed || me.Dead)
            {
                continue;
            }

            PawnState? best = null;
            var bestDistance = int.MaxValue;
            foreach (var other in state.Pawns)
            {
                if (other.Downed || other.Dead || sideOf[other.Id.Value] == sideOf[me.Id.Value])
                {
                    continue;
                }

                var dx = other.Pos.X - me.Pos.X;
                var dz = other.Pos.Z - me.Pos.Z;
                var d = (dx * dx) + (dz * dz);
                if (d < bestDistance)
                {
                    best = other;
                    bestDistance = d;
                }
            }

            if (best is not { } b || (target.TryGetValue(me.Id.Value, out var current) && current == b.Id.Value))
            {
                continue;
            }

            target[me.Id.Value] = b.Id.Value;
            orders.Add(new Order { Pawn = me.Id, Kind = OrderKind.Attack, Target = b.Id });
        }

        return orders;
    }
}
