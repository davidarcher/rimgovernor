using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// Measurement runs alone through its own -method/-parallel none command: see docs/developers/testing/portfolio-allocations.md.
public sealed class PortfolioAllocationTests
{
    /// <summary>The matrix's outcome-evaluation budgets (#3722), each with the default traversal bound: a seed-constrained row, two
    /// extension-constrained rows, the 2,048 the one-day four-worker derivation gave, the 8,192 production default, the row that
    /// converges the four-worker fixture and finishes the eight-root fixture's sweep, and a ceiling nothing reaches. They are not the
    /// former expansion rows restated: see portfolio-allocations.md.</summary>
    internal static readonly int[] Budgets = [256, 1024, 2048, 4096, 8192, 16384, 65536];
    /// <summary>The traversal bounds the production-horizon and planned-shelter matrices cross with <see cref="Budgets"/> (#3725): one
    /// full-bound schedule search (4,096, the per-portfolio scheduler bound), the expansion default before #3563 (16,384) and the
    /// production bound (65,536).</summary>
    internal static readonly int[] Traversals = [4096, 16384, ForecastSearchBudget.DefaultTraversal];
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new FactJsonConverter<double>(), new FactJsonConverter<int>(), new FactJsonConverter<bool>() }
    };

    internal static RoundsFacts LargerFacts()
    {
        var facts = ProductiveLearningTests.NormalFoodFacts();
        var roster = WorkResourceTestFacts.Create("pawn", "helper", "diner", "porter");
        var needs = facts.ForecastObserved.Needs!;
        var read = facts.ForecastObserved.Coupled!;
        var novice = read.FoodConversions.Single(c => c.Pawn == "pawn");
        var experienced = read.FoodConversions.Single(c => c.Pawn == "helper");
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 16)]),
            WorkResources = roster.WorkResources,
            ForecastObserved = facts.ForecastObserved with
            {
                WorkPawns = roster.ForecastObserved.WorkPawns,
                Needs = needs with { Pawns = [.. needs.Pawns, needs.Pawns[0] with { Pawn = "diner" }, needs.Pawns[1] with { Pawn = "porter" }] },
                Coupled = read with
                {
                    FoodSources = EquatableArray.From(read.FoodSources.Select(s => s with { Count = 16 })),
                    FoodConversions = [novice with { AvailableInput = 16 }, experienced with { AvailableInput = 16 },
                        experienced with { Pawn = "diner", WorkTicks = Known(105d), AvailableInput = 16 },
                        experienced with { Pawn = "porter", WorkTicks = Known(110d), AvailableInput = 16 }],
                    FoodUses = [.. read.FoodUses, read.FoodUses[0] with { Pawn = "diner" }],
                    Routes = EquatableArray.From(read.Routes.Concat(read.Routes.Where(r => r.Pawn == "helper")
                        .SelectMany(r => new[] { r with { Pawn = "diner" }, r with { Pawn = "porter" } })))
                }
            }
        };
        return WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
    }

    internal static ColonyForecastRequest Request(RoundsFacts facts, ForecastCoupledCandidates generated, double horizon = 700) =>
        ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), horizon / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated },  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());

    private static object Quality(ForecastCoupledCandidates generated, ForecastCoupledSelection selection) => new
    {
        Workers = generated.Context.Activities.Pawns.Length,
        Tasks = generated.Context.Activities.Activities.Length,
        Options = generated.Context.Activities.Options.Length,
        Roots = generated.Alternatives.Count,
        selection.CompositionExpansions, selection.GenerationTruncated,
        selection.Selection.Expansions, selection.Selection.SearchTruncated,
        selection.OutcomeEvaluations, selection.BaselineEvaluations, selection.BaselineExpansions,
        SearchPhases = selection.SearchPhases.Select(p => new { p.Phase, p.Expansions, p.ScheduleSearches, p.OutcomeEvaluations, p.Pruned }).ToArray(),
        selection.Selection.Selected,
        selection.Selected.Feasible,
        selection.Selected.Plan.CompletedAt,
        selection.Selected.Plan.LaborCost,
        Physical = selection.Selected.Plan.Allocations.Select(a => new
        {
            a.Activity, a.Pawn, a.Depart, a.Arrive, a.Start, a.End, a.TravelTicks, a.WaitTicks, a.WorkTicks,
            a.DisplacedLaborTicks, a.RecoveryLaborTicks, a.Learning
        }).ToArray(),
        selection.BaselineVector, selection.SelectedVector,
        selection.Selection.Findings
    };

    [Fact]
    public void MeasureWarmedAllocationMatrix()
    {
        var output = Environment.GetEnvironmentVariable("RIMGOVERNOR_PORTFOLIO_OUTPUT");
        if (string.IsNullOrEmpty(output)) return; // Explicit opt-in diagnostics; ordinary checks run deterministic cases above.
        var metadata = new
        {
            Runtime = RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture, Environment.ProcessorCount, Environment.MachineName,
            Configuration = typeof(PortfolioAllocationTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
            Protocol = "Synchronous same thread; one warmup then three samples; no forced GC; setup/request and formatting outside selector interval; generation includes composition; profiling is a separate pass.",
            BaselineRevision = "3d130260b",
            MeasuredAssemblyVersion = typeof(PortfolioAllocationTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion
        };
        File.WriteAllText(Path.Combine(output, "environment.json"), JsonSerializer.Serialize(metadata, Json));
        foreach (var larger in new[] { false, true })
        {
            var name = larger ? "four-workers" : "productive";
            var facts = larger ? LargerFacts() : ProductiveLearningTests.NormalFoodFacts();
            var generation = Measure(() => ForecastCoupledCandidates.GenerateCoupledActivities(facts, 700)!);
            var generated = generation.Result.Priced(700);
            var request = Request(facts, generated);
            var rows = Budgets.Select(budget =>
            {
                var measured = Measure(() => ForecastCoupledSelection.CompareActivityOutcomes(request, generated, new(budget)));
                return new { EvaluationBudget = budget, Traversal = ForecastSearchBudget.DefaultTraversal, measured.Bytes, measured.Milliseconds, Quality = Quality(generated, measured.Result) };
            }).ToArray();
            File.WriteAllText(Path.Combine(output, name + ".measurements.json"), JsonSerializer.Serialize(new
            {
                DemandedMeals = larger ? 4 : 2,
                GenerationBytes = generation.Bytes, GenerationMilliseconds = generation.Milliseconds, Rows = rows
            }, Json));
            // The pinned baseline (Baselines/<fixture>.quality.json): the rows without their timings, ready to copy in when re-pinning.
            File.WriteAllText(Path.Combine(output, name + ".quality.json"), JsonSerializer.Serialize(rows.Select(r => new { r.EvaluationBudget, r.Traversal, r.Quality }).ToArray(), Json));
            // One-day horizon (#3467): the production review horizon, over the budgets and traversal bounds (#3725); the production
            // default is the 8,192 x 65,536 row.
            var day = Measure(() => ForecastCoupledCandidates.GenerateCoupledActivities(facts, GameTime.TicksPerDay)!);
            WriteMatrix(Path.Combine(output, name + ".one-day.measurements.json"), facts, day.Result.Priced(GameTime.TicksPerDay), GameTime.TicksPerDay, day);
        }
        // The third workload (#3725): the planned-shelter fixture as its tests build it, at its own horizon. Its ten roots' schedule
        // searches cost a few expansions each, so the cardinality sweep is affordable and the budget decides whether it runs (the
        // fixture family #3722's 2,048 attempt regressed). Generation is measured apart; the matrix uses the fixture's own candidates.
        var shelter = PlannedShelterTests.UnplacedHeatedRoom();
        var shelterGeneration = Measure(() => ForecastCoupledCandidates.GenerateCoupledActivities(shelter, PlannedShelterTests.Horizon)!);
        WriteMatrix(Path.Combine(output, "planned-shelter.measurements.json"), shelter, shelter.CoupledAlternatives!, PlannedShelterTests.Horizon, shelterGeneration);
    }

    /// <summary>Selection over <see cref="Budgets"/> x <see cref="Traversals"/> on one generated workload: each row's budget in both units, its
    /// three allocation and time samples and the complete selected quality, after the generation's own samples.</summary>
    private static void WriteMatrix(string path, RoundsFacts facts, ForecastCoupledCandidates generated, double horizon,
        (ForecastCoupledCandidates Result, long[] Bytes, double[] Milliseconds) generation)
    {
        var request = Request(facts, generated, horizon);
        var rows = Traversals.SelectMany(traversal => Budgets.Select(budget =>
        {
            var measured = Measure(() => ForecastCoupledSelection.CompareActivityOutcomes(request, generated, new(budget, traversal)));
            return new { EvaluationBudget = budget, Traversal = traversal, measured.Bytes, measured.Milliseconds, Quality = Quality(generated, measured.Result) };
        })).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Horizon = horizon, GenerationBytes = generation.Bytes, GenerationMilliseconds = generation.Milliseconds, Rows = rows
        }, Json));
    }

    [Fact]
    public void ProfileAllocationTypes()
    {
        var output = Environment.GetEnvironmentVariable("RIMGOVERNOR_PORTFOLIO_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        foreach (var larger in new[] { false, true })
        {
            var name = larger ? "four-workers" : "productive";
            var facts = larger ? LargerFacts() : ProductiveLearningTests.NormalFoodFacts();
            var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 700)!.Priced(700);
            var request = Request(facts, generated);
            ForecastCoupledSelection.CompareActivityOutcomes(request, generated); // Warm both stages before enabling the listener.
            foreach (var stage in new[] { "generation", "selection" })
            {
                using var profile = new AllocationSamples();
                for (var i = 0; i < (stage == "generation" ? 100 : 3); i++)
                {
                    if (stage == "generation") ForecastCoupledCandidates.GenerateCoupledActivities(facts, 700);
                    else ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
                }
                var samples = profile.Snapshot();
                File.WriteAllText(Path.Combine(output, name + "." + stage + ".allocation-types.json"), JsonSerializer.Serialize(samples, Json));
            }
            // One-day horizon selection (#3468), the production review horizon.
            var day = ForecastCoupledCandidates.GenerateCoupledActivities(facts, GameTime.TicksPerDay)!.Priced(GameTime.TicksPerDay);
            var dayRequest = Request(facts, day, GameTime.TicksPerDay);
            ForecastCoupledSelection.CompareActivityOutcomes(dayRequest, day);
            using (var profile = new AllocationSamples())
            {
                ForecastCoupledSelection.CompareActivityOutcomes(dayRequest, day);
                File.WriteAllText(Path.Combine(output, name + ".selection-one-day.allocation-types.json"), JsonSerializer.Serialize(profile.Snapshot(), Json));
            }
        }
    }
    private static (T Result, long[] Bytes, double[] Milliseconds) Measure<T>(Func<T> operation)
    {
        var result = operation();
        var bytes = new long[3]; var elapsed = new double[3];
        for (var i = 0; i < bytes.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            result = operation();
            var end = Stopwatch.GetTimestamp();
            bytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            elapsed[i] = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
        }
        return (result, bytes, elapsed);
    }

    private sealed record AllocationRow(string Type, long SampledBytes, long Samples, string[] CompilerMethods);

    private sealed class AllocationSamples : EventListener
    {
        private readonly ConcurrentDictionary<string, (long Bytes, long Samples)> samples = new(StringComparer.Ordinal);
        private readonly ManualResetEventSlim drained = new();
        private volatile bool draining;
        private volatile bool capture = true;
        public override void Dispose()
        {
            base.Dispose();
            drained.Dispose();
        }
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)1);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (draining && data.EventName?.StartsWith("GCHeapStats", StringComparison.Ordinal) == true) drained.Set();
            if (!capture || samples is null || data.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || data.PayloadNames is null || data.Payload is null) return;
            var typeIndex = data.PayloadNames.IndexOf("TypeName");
            var bytesIndex = data.PayloadNames.IndexOf("AllocationAmount64");
            if (typeIndex < 0 || bytesIndex < 0 || data.Payload[typeIndex] is not string type) return;
            var count = Convert.ToInt64(data.Payload[bytesIndex], System.Globalization.CultureInfo.InvariantCulture);
            samples.AddOrUpdate(type, (count, 1), (_, old) => (old.Bytes + count, old.Samples + 1));
        }
        internal AllocationRow[] Snapshot()
        {
            // A post-workload GC marker drains earlier runtime events before the snapshot. Never in a timed interval.
            draining = true;
            GC.Collect();
            Assert.True(drained.Wait(TimeSpan.FromSeconds(5)), "CLR allocation stream did not deliver the drain marker");
            capture = false;
            Assert.NotEmpty(samples);
            return samples.ToArray().OrderByDescending(p => p.Value.Bytes).Select(p => new AllocationRow(p.Key, p.Value.Bytes, p.Value.Samples,
                typeof(ColonyForecastRules).Assembly.GetTypes().Where(t => t.IsNested && !t.IsNestedPublic && t.DeclaringType!.Name.EndsWith("Rules", StringComparison.Ordinal) && t.Name == p.Key)
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    .Select(m => m.Name).ToArray())).ToArray();
        }
    }
}
