using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.Combat.Import;
using RimGovernor.RealCombat;

internal sealed record Case(Spec Spec, int MaxTicks);

internal static class Cases
{
    private static readonly IReadOnlyDictionary<string, int> Skilled = new Dictionary<string, int> { ["Shooting"] = 8, ["Melee"] = 8 };

    internal static IEnumerable<Case> All(RimWorld.Difficulty difficulty)
    {
        yield return Duel(difficulty, "duel15", 15);
        yield return Duel(difficulty, "duel25", 25);
        yield return Squads(difficulty, "squad2v2", 2);
        yield return Squads(difficulty, "squad3v3", 3);
    }

    private static Spec Base(RimWorld.Difficulty d, string name) => new()
    {
        Name = name,
        Width = 60,
        Height = 60,
        Terrain = "Soil",
        Weather = "Clear",
        FriendlyFireChanceFactor = Fact.Known((double)d.friendlyFireChanceFactor),
        DeathOnDowned = Fact.Known(new DeathOnDowned { PopulationIntent = 0, EnemyDeathOnDownedChanceFactor = d.enemyDeathOnDownedChanceFactor, UnwaveringPrisoners = d.unwaveringPrisoners }),
        AllowInstantKillChance = Fact.Known(1.0),
        StepTicks = 1,
        MaxTicks = 6000,
    };

    private static SpecPawn Rifleman(string id, string side, string kind, int x, int z) => new()
    {
        Id = new PawnId(id),
        Side = side,
        Kind = kind,
        Cell = new Cell(x, z),
        Weapon = "Gun_BoltActionRifle",
        Worn = Fact.Known<IReadOnlyList<Wear>>([]),
        Skills = Skilled,
    };

    private static Case Duel(RimWorld.Difficulty d, string name, int gap) =>
        new(
            Base(d, name) with
            {
                Pawns = [Rifleman("col", "colonist", "Colonist", 20, 30), Rifleman("host", "hostile", "Pirate", 20 + gap, 30)],
            },
            6000);

    // n riflemen a side, three cells apart along a column, facing across the gap.
    private static Case Squads(RimWorld.Difficulty d, string name, int n) =>
        new(
            Base(d, name) with
            {
                Pawns =
                [
                    .. Enumerable.Range(0, n).Select(i => Rifleman("col" + i, "colonist", "Colonist", 20, 27 + (i * 3))),
                    .. Enumerable.Range(0, n).Select(i => Rifleman("host" + i, "hostile", "Pirate", 40, 27 + (i * 3))),
                ],
            },
            9000);
}

internal static class Convert
{
    internal static RealFightRequest ToRequest(Spec s, IReadOnlyList<int> seeds, int maxTicks) => new()
    {
        Name = s.Name,
        Width = s.Width,
        Height = s.Height,
        Terrain = s.Terrain,
        Weather = s.Weather,
        Things = [.. s.Things.Select(t => new RealThing(t.Def, "", t.Cell.X, t.Cell.Z))],
        Pawns =
        [
            .. s.Pawns.Select(p => new RealPawn
            {
                Id = p.Id.Value,
                Side = p.Side,
                Kind = p.Kind,
                X = p.Cell.X,
                Z = p.Cell.Z,
                Weapon = p.Weapon,
                WeaponStuff = p.WeaponStuff,
                Worn = [.. p.Worn.Value.Select(w => new RealWear(w.Def, w.Stuff))],
                Skills = p.Skills ?? new Dictionary<string, int>(),
                Traits = [.. p.Traits.Select(t => new RealTrait(t.Def, t.Degree))],
            }),
        ],
        Seeds = seeds,
        MaxTicks = maxTicks,
    };
}

// The outcomes of many fights of one case, whichever engine played them.
internal sealed class Agg
{
    internal int Colonist;
    internal int Hostile;
    internal int Unresolved;
    internal readonly List<int> Ticks = [];
    internal readonly Dictionary<string, long> Shots = [];
    internal readonly Dictionary<string, long> Hits = [];

    internal int N => Ticks.Count;

    internal void Add(string winner, int ticks, Dictionary<string, int> shots, Dictionary<string, int> hits)
    {
        if (winner == "colonist")
        {
            Colonist++;
        }
        else if (winner == "hostile")
        {
            Hostile++;
        }
        else
        {
            Unresolved++;
        }

        Ticks.Add(ticks);
        foreach (var (k, v) in shots)
        {
            Shots[k] = Shots.GetValueOrDefault(k) + v;
        }

        foreach (var (k, v) in hits)
        {
            Hits[k] = Hits.GetValueOrDefault(k) + v;
        }
    }

    internal static Agg From(RealFightRequest request, IReadOnlyList<RealSeedOutcome> outcomes)
    {
        var sideOf = request.Pawns.ToDictionary(p => p.Id, p => p.Side);
        var agg = new Agg();
        foreach (var o in outcomes)
        {
            var shots = new Dictionary<string, int>();
            var hits = new Dictionary<string, int>();
            foreach (var p in o.Pawns)
            {
                shots[sideOf[p.Id]] = shots.GetValueOrDefault(sideOf[p.Id]) + p.Shots;
                hits[sideOf[p.Id]] = hits.GetValueOrDefault(sideOf[p.Id]) + p.HitsTaken;
            }

            agg.Add(o.Winner, o.Ticks, shots, hits);
        }

        return agg;
    }

    internal int Quantile(double q) => Ticks.Order().ElementAt((int)Math.Min(Ticks.Count - 1, Ticks.Count * q));
}

internal static class Report
{
    // Prints both engines' distributions and checks the gated metrics; true when all agree.
    internal static bool Compare(string name, Agg real, Agg port, long realMs, long portMs)
    {
        Console.Error.WriteLine($"== {name}  n={real.N}");
        Row("real", real, realMs);
        Row("sim ", port, portMs);
        var pooled = (real.Colonist + port.Colonist) / (double)(real.N + port.N);
        var tolerance = Math.Max(0.02, 3 * Math.Sqrt(2 * pooled * (1 - pooled) / real.N));
        var winDiff = (real.Colonist - port.Colonist) / (double)real.N;
        var ok = Gate("colonist win rate", Math.Abs(winDiff) <= tolerance, $"real {real.Colonist / (double)real.N:P1} vs sim {port.Colonist / (double)port.N:P1}, diff {winDiff:+0.0%;-0.0%} (tolerance {tolerance:P1})");
        ok &= Gate("median ticks", Within(real.Quantile(0.5), port.Quantile(0.5), 0.05), $"real {real.Quantile(0.5)} vs sim {port.Quantile(0.5)}");
        ok &= Gate("mean ticks", Within(real.Ticks.Average(), port.Ticks.Average(), 0.05), $"real {real.Ticks.Average():0} vs sim {port.Ticks.Average():0}");
        return ok;
    }

    private static bool Within(double a, double b, double fraction) => Math.Abs(a - b) <= fraction * Math.Max(a, b);

    private static bool Gate(string metric, bool pass, string detail)
    {
        Console.Error.WriteLine($"   {(pass ? "PASS" : "FAIL")}  {metric}: {detail}");
        return pass;
    }

    private static void Row(string engine, Agg a, long ms)
    {
        var shots = string.Join("/", a.Shots.OrderBy(k => k.Key).Select(k => $"{k.Value / (double)a.N:0.0}"));
        var hits = string.Join("/", a.Hits.OrderBy(k => k.Key).Select(k => $"{k.Value / (double)a.N:0.0}"));
        Console.Error.WriteLine($"   {engine} colonist {a.Colonist} hostile {a.Hostile} unresolved {a.Unresolved} | ticks p10 {a.Quantile(0.1)} median {a.Quantile(0.5)} p90 {a.Quantile(0.9)} mean {a.Ticks.Average():0} | per fight shots(col/host) {shots} hits-taken(col/host) {hits} | {ms} ms");
    }
}
