using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>One finished <see cref="CombatRunner.RunFight"/>.</summary>
public sealed record FightResult
{
    /// <summary>Scores the fight with the shared combat metrics definition.</summary>
    public CombatMetrics.Metrics Metrics { get; init; } = new();

    /// <summary>True when one side was all down or dead; <see cref="Winner"/> names the side left standing.</summary>
    public bool Resolved { get; init; }

    public string Winner { get; init; } = "";

    /// <summary>The sim tick the fight ended on.</summary>
    public int Ticks { get; init; }

    /// <summary>The raiders that left the map (fled).</summary>
    public int Left { get; init; }

    /// <summary>DecideCombat stops (the first decision and the backstops included).</summary>
    public int Decisions { get; init; }

    /// <summary>The policy's orders.</summary>
    public int Orders { get; init; }

    /// <summary>The sim's refusals of them.</summary>
    public int Refused { get; init; }

    /// <summary>Refused orders by reason.</summary>
    public IReadOnlyDictionary<string, int> Refusals { get; init; } = new Dictionary<string, int>();

    /// <summary>The policy's memory at the end.</summary>
    public CombatMemory Memory { get; init; } = new();
}

/// <summary>Plays a <see cref="Fight"/> against the unchanged policy.</summary>
public static class CombatRunner
{
    // The decision cadence when a Fight sets none.
    private const int DefaultBackstopTicks = 300;

    /// <summary>
    /// Plays <paramref name="f"/> from the seed to resolution or f.MaxTicks, asking the unchanged
    /// <c>DecideCombat</c> for orders at the first tick, at every stop the adapter raises and every backstop,
    /// and stepping the sim between. The tuning rides in the policy's memory as a search trial's does. A Fight
    /// the sim cannot represent throws <see cref="UnsupportedScenarioException"/> before any step; an invalid
    /// tuning or a non-positive MaxTicks throws <see cref="DomainException"/>.
    /// </summary>
    public static FightResult RunFight(Fight f, ulong seed, CombatTuning tuning, CancellationToken cancellationToken = default)
    {
        tuning.Validate();
        if (f.MaxTicks <= 0)
        {
            throw new DomainException("combatsim: Fight.MaxTicks must be positive");
        }
        var backstop = f.BackstopTicks > 0 ? f.BackstopTicks : DefaultBackstopTicks;
        var sim = new Sim();
        sim.Reset(f.Scenario, seed);
        var a = new Adapter(f, sim);
        EnemyAi? ai = f.Raid is null ? null : new EnemyAi(sim, f.Raid);
        a.ScanRange(); // the first scan only records who is already in range
        var refusals = new Dictionary<string, int>();
        var memory = new CombatMemory { Tuning = tuning };
        StopEvent? stop = new();
        var due = true;
        var last = 0;
        int decisions = 0, ordersCount = 0, refused = 0;
        var carried = new List<Order>();
        string winner;
        bool resolved;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (due)
            {
                List<CombatOrder> orders;
                (orders, memory) = a.Decide(stop ?? new StopEvent(), memory);
                var (results, simOrders) = a.Apply(orders);
                memory = a.Remember(memory, results);
                carried.AddRange(simOrders);
                decisions++;
                ordersCount += orders.Count;
                foreach (var r in results)
                {
                    if (!r.Applied)
                    {
                        refused++;
                        refusals[r.Refusal] = refusals.GetValueOrDefault(r.Refusal) + 1;
                    }
                }
                last = sim.TickCount;
                due = false;
            }

            // The policy's orders apply first: the holders' Autonomy reads the pawns as the orders left them,
            // as Wait_Combat does after a new job replaces it, so a pawn just told to walk does not also open
            // fire.
            sim.Apply(carried);
            carried.Clear();
            var stepOrders = a.Autonomy();
            if (ai is not null)
            {
                stepOrders.AddRange(ai.Orders());
            }
            if (f.Scripted is not null)
            {
                stepOrders.AddRange(f.Scripted(sim.Snapshot()));
            }
            var (_, events) = sim.Step(stepOrders);
            f.Trace?.Invoke(events);
            ai?.Observe();
            (winner, resolved) = sim.Resolved();
            if (resolved || sim.TickCount >= f.MaxTicks)
            {
                break;
            }
            stop = a.Stop(events);
            due = stop is not null;
            if (!due && sim.TickCount - last >= backstop)
            {
                stop = new StopEvent();
                due = true;
            }
        }
        var left = 0;
        foreach (var p in sim.Pawns)
        {
            if (p.Left)
            {
                left++;
            }
        }
        return new FightResult
        {
            Metrics = sim.Metrics(f.Name),
            Resolved = resolved,
            Winner = winner,
            Ticks = sim.TickCount,
            Left = left,
            Decisions = decisions,
            Orders = ordersCount,
            Refused = refused,
            Refusals = refusals,
            Memory = memory,
        };
    }
}
