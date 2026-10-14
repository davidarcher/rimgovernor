using System.Diagnostics;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.Combat;
using RimGovernor.Brain.Simulation.Combat.Import;
using RimGovernor.RealCombat;

// RimGovernor.RealCombat.Parity --game <dir> [--seeds N] [--seed0 S] [--case name]
// Plays each case's Spec through RimWorld's own combat and through the hand-ported sim over the same seed numbers and
// compares the outcome distributions. The two engines draw from different generators, so a single seed is not
// comparable; a win rate or a tick quantile is. Exit 1 when a gated metric leaves its tolerance.
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.SetOut(Console.Error); // the game logs to stdout
        string game = null, only = null;
        var n = 1000;
        var seed0 = 5000;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--game": game = args[i + 1]; break;
                case "--seeds": n = int.Parse(args[i + 1]); break;
                case "--seed0": seed0 = int.Parse(args[i + 1]); break;
                case "--case": only = args[i + 1]; break;
                default: Console.Error.WriteLine("unknown argument " + args[i]); return 2;
            }
        }

        if (game is null || only is null)
        {
            Console.Error.WriteLine("--game and --case are required (one case per process: each case builds its own map). cases: duel15 duel25 squad2v2 squad3v3");
            return 2;
        }

        GameHost.Boot(game);
        var sim = new SimSide();
        var seeds = Enumerable.Range(seed0, n).ToList();
        var failed = false;
        foreach (var c in Cases.All(sim.Difficulty))
        {
            if (only is not null && c.Spec.Name != only)
            {
                continue;
            }

            var request = Convert.ToRequest(c.Spec, seeds, c.MaxTicks);
            var arena = new Arena(request);
            var clock = Stopwatch.StartNew();
            var real = Agg.From(request, seeds.Select(arena.Fight).ToList());
            var realMs = clock.ElapsedMilliseconds;
            clock.Restart();
            var port = sim.Run(c.Spec, seeds, c.MaxTicks);
            Console.Out.Flush();
            failed |= !Report.Compare(c.Spec.Name, real, port, realMs, clock.ElapsedMilliseconds);
        }

        return failed ? 1 : 0;
    }
}
