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
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

// Measurement runs alone through its own -method/-parallel none command: see docs/developers/testing/portfolio-allocations.md.
public sealed class PortfolioAllocationTests
{
    private static readonly int[] Budgets = [1024, 4096, 8192, 16384, 65536, 262144];
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
        return ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
    }

    internal static ColonyForecastRequest Request(RoundsFacts facts, ForecastCoupledCandidates generated) =>
        CoupledForecastRequestOf(new("world/map/load", 100), 700d / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated }, new(), new() { Work = ForecastWorkInputsOf(facts) }, [], new());

    private static object Quality(ForecastCoupledCandidates generated, ForecastCoupledSelection selection) => new
    {
        Workers = generated.Context.Activities.Pawns.Length,
        Tasks = generated.Context.Activities.Activities.Length,
        Options = generated.Context.Activities.Options.Length,
        Roots = generated.Alternatives.Count,
        selection.CompositionExpansions, selection.GenerationTruncated,
        selection.Selection.Expansions, selection.Selection.SearchTruncated,
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
            var generation = Measure(() => GenerateCoupledActivities(facts, 700)!);
            var generated = generation.Result;
            var request = Request(facts, generated);
            var rows = new List<object>();
            foreach (var budget in Budgets)
            {
                var measured = Measure(() => CompareActivityOutcomes(request, generated, budget));
                rows.Add(new { Budget = budget, measured.Bytes, measured.Milliseconds, Quality = Quality(generated, measured.Result) });
            }
            File.WriteAllText(Path.Combine(output, name + ".measurements.json"), JsonSerializer.Serialize(new
            {
                DemandedMeals = larger ? 4 : 2,
                GenerationBytes = generation.Bytes, GenerationMilliseconds = generation.Milliseconds, Rows = rows
            }, Json));
        }
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
            var generated = GenerateCoupledActivities(facts, 700)!;
            var request = Request(facts, generated);
            CompareActivityOutcomes(request, generated); // Warm both stages before enabling the listener.
            foreach (var stage in new[] { "generation", "selection" })
            {
                using var profile = new AllocationSamples();
                for (var i = 0; i < (stage == "generation" ? 100 : 3); i++)
                {
                    if (stage == "generation") GenerateCoupledActivities(facts, 700);
                    else CompareActivityOutcomes(request, generated);
                }
                var samples = profile.Snapshot();
                File.WriteAllText(Path.Combine(output, name + "." + stage + ".allocation-types.json"), JsonSerializer.Serialize(samples, Json));
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
                typeof(PolicyFunctions).GetNestedTypes(BindingFlags.NonPublic).Where(t => t.Name == p.Key)
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    .Select(m => m.Name).ToArray())).ToArray();
        }
    }
}
