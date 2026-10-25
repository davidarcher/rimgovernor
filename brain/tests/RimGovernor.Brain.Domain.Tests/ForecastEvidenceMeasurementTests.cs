using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// Opt-in, isolated measurement; never a latency gate. Shares the portfolio fixtures and protocol.
public sealed class ForecastEvidenceMeasurementTests
{
    private static readonly GenerationSnapshot Scope = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [Fact]
    public async Task MeasureForecastEvidenceOverhead()
    {
        var output = Environment.GetEnvironmentVariable("RIMGOVERNOR_PORTFOLIO_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        foreach (var larger in new[] { false, true })
        {
            var name = larger ? "four-workers" : "productive";
            var facts = larger ? PortfolioAllocationTests.LargerFacts() : ProductiveLearningTests.NormalFoodFacts();
            var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, GameTime.TicksPerDay)!.Priced(GameTime.TicksPerDay);
            var request = PortfolioAllocationTests.Request(facts, generated, GameTime.TicksPerDay);
            var policy = Measure(() => ForecastCoupledSelection.CompareActivityOutcomes(request, generated));
            var selection = policy.Result;
            var context = generated.Context;
            var path = Path.Combine(output, name + ".evidence-" + Guid.NewGuid().ToString("N"));
            object capture, retirement;
            int pending;
            // Production emitter and writer; thread samples include enqueue, not the asynchronous encoder/file writer.
            await using (var writer = OtlpFileWriter.Start(new() { Dir = path }))
            {
                var emitter = new TelemetryEmitter(writer);
                ForecastEvidencePending Owner() => new(
                    p => emitter.Decide(new() { Kind = TelemetryKinds.ForecastPrediction.Name, Component = "rounds", Target = p.Key.Sample,
                        Verdict = "captured", Reason = "selected_forecast", Attrs = ForecastEvidenceReader.Fields(p) }),
                    o => emitter.Decide(new() { Kind = TelemetryKinds.ForecastObservation.Name, Component = "rounds", Target = o.Key.Sample,
                        Verdict = o.Value is EvidenceValue.Valued || o.Completion is ForecastCompletion.Interval ? "observed" : "unknown",
                        Reason = o.Value is EvidenceValue.Unavailable { Reason: { Length: > 0 } reason } ? reason : "native_observation", Attrs = ForecastEvidenceReader.Fields(o) }));
                var owner = Owner();
                owner.Review(Scope, 100, context.Needs, context);
                ulong revision = 0;
                var captured = Measure(() => { owner.Capture(Scope, 100, ++revision, selection, context, ForecastAdmission.Selected, [], ""); return owner.Count; });
                capture = new { captured.Bytes, captured.Milliseconds };
                pending = owner.Count;
                // Recreate the same four captures before each measured retirement; setup excluded from sample.
                retirement = MeasurePrepared(() => {
                    var fresh = Owner(); fresh.Review(Scope, 100, context.Needs, context);
                    for (ulong i = 1; i <= 4; i++) fresh.Capture(Scope, 100, i, selection, context, ForecastAdmission.Selected, [], "");
                    return fresh;
                }, fresh => fresh.Review(Scope, 60100, context.Needs with { Tick = new(60100) }, context));
            }
            var log = Path.Combine(path, "logs.jsonl");
            var reader = Measure(() => ForecastEvidenceReader.Read(log));
            var report = Measure(() => JsonSerializer.Serialize(ForecastResidualReport.Build(reader.Result)));
            // Retained bytes use forced collections only outside timed operations; the owner references pre-existing selection values.
            var before = GC.GetTotalMemory(true);
            var bounded = new ForecastEvidencePending(_ => { }, _ => { });
            bounded.Review(Scope, 100, context.Needs, context);
            ulong capacityRevision = 0;
            while (bounded.Count < ForecastEvidencePending.Capacity && capacityRevision < 4096)
                bounded.Capture(Scope, 100, ++capacityRevision, selection, context, ForecastAdmission.Selected, [], "");
            var retained = GC.GetTotalMemory(true) - before;
            var count = bounded.Count;
            GC.KeepAlive(bounded);
            File.WriteAllText(Path.Combine(output, name + ".evidence.measurements.json"), JsonSerializer.Serialize(new {
                Runtime = RuntimeInformation.FrameworkDescription, Environment.MachineName,
                Assembly = typeof(ForecastEvidenceMeasurementTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                Protocol = "One warmup, three synchronous same-thread samples; no forced GC in timed intervals. No native reads, full ReviewState dispatch, task binding or async sink allocation included. Unbound tasks are explicit gaps. Retained bytes are descriptive process-wide GC estimate.",
                Horizon = GameTime.TicksPerDay, Budget = "production default", Workers = context.Needs.Pawns.Count,
                Policy = new { policy.Bytes, policy.Milliseconds }, Capture = capture, Retirement = retirement,
                PendingAfterFourCaptures = pending, Capacity = ForecastEvidencePending.Capacity, Populated = count, RetainedBytes = retained,
                TelemetryBytes = new FileInfo(log).Length, Predictions = reader.Result.Predictions.Count, Observations = reader.Result.Observations.Count,
                Reader = new { reader.Bytes, reader.Milliseconds }, Report = new { report.Bytes, report.Milliseconds }, Log = log
            }, Json));
        }
    }

    private static (T Result, long[] Bytes, double[] Milliseconds) Measure<T>(Func<T> action)
    {
        var result = action(); var bytes = new long[3]; var times = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
            result = action(); times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            bytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        return (result, bytes, times);
    }
    private static object MeasurePrepared<T>(Func<T> prepare, Action<T> action)
    {
        action(prepare()); var bytes = new long[3]; var times = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var state = prepare(); var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
            action(state); times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            bytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        return new { Bytes = bytes, Milliseconds = times };
    }
}
