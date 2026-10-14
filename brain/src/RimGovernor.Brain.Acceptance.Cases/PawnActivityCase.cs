using System.Globalization;
using System.Diagnostics;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class PawnActivityCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/pawn-activity",
        Scope = "Native job transitions separate travel from harvest work and link an observed product to the worker. Pure snapshots cannot prove Harmony lifecycle hooks or physical delivery.",
        Start = new FixtureStart { Op = "test/pawn_activity_prepare", On = new LabStart() },
        RequiredOps = ["test/pawn_activity_prepare", "test/pawn_activity_read"],
        QuietWorld = true, Budget = TimeSpan.FromMinutes(4), Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken ct)
    {
        var prepared = s.Prepared ?? throw new AcceptanceException("Missing activity fixture.");
        var world = prepared["world"].AsString();
        var pawn = prepared["pawn"].AsString();
        var from = (long)prepared["start"].AsNumber();
        var logs = Path.Combine(GameSessions.EndpointDirectory(s.Config.Configuration, s.Config.GameId), "logs.jsonl");
        for (var ticks = 0; ticks < 3600; ticks += 120)
        {
            _ = await s.AdvanceAsync(120, null, ct).ConfigureAwait(false);
            var state = await s.Harness.CallAsync("activity-" + ticks.ToString(CultureInfo.InvariantCulture),
                "test/pawn_activity_read", [], ct).ConfigureAwait(false);
            var to = (long)state["tick"].AsNumber();
            var rows = PawnActivityReport.Read(logs, world, pawn, from, to);
            var delivered = OtlpTimeline.Read(logs).Any(r => r.Kind == PawnActivityTelemetry.OutputKind
                && r.Payload.GetValueOrDefault("pawn") as string == pawn);
            if (state["produced"].AsNumber() <= 0 || !delivered
                || !rows.Any(r => r.Activity == "travel") || !rows.Any(r => r.Activity == "work")) continue;
            var telemetryCost = await MeasureTelemetryAsync(s.Harness.Output, rows.First(r => !r.Gap), ct).ConfigureAwait(false);
            s.Report["activity"] = new JsonObject
            {
                ["from"] = from, ["to"] = to, ["produced"] = state["produced"]?.DeepClone(),
                ["baseline_sample_ms"] = state["baseline_sample_ms"]?.DeepClone(),
                ["sample_and_accounting_ms"] = state["sample_and_accounting_ms"]?.DeepClone(),
                ["benchmark_note"] = state["note"]?.DeepClone(),
                ["telemetry_overhead"] = telemetryCost,
                ["unknown_ticks"] = rows.Where(r => r.Gap || r.Activity == "unknown").Sum(r => r.End - r.Start),
            };
            return;
        }
        throw new AcceptanceException("No complete travel/work/product evidence within 3600 ticks; inspect pawn_activity rows and tracker faults.");
    }
    private static async Task<JsonObject> MeasureTelemetryAsync(string output, PawnActivityReportRow observed, CancellationToken ct)
    {
        // Same observed fixture interval on both paths. The benchmark ring is an evidence artifact,
        // never merged into the colony history. Includes enqueue, encoding and final file flush.
        const int iterations = 512;
        var sample = new PawnActivitySample(observed.World, observed.Pawn, observed.Start, observed.Job,
            1, "benchmark", observed.Activity, observed.Purpose, "", "");
        var baseline = new TelemetryEmitter(NullTelemetrySink.Instance);
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) PawnActivityTelemetry.Write(baseline, new(i + 1, sample, observed.End, "", observed.Outcome, 15));
        var baselineMs = timer.Elapsed.TotalMilliseconds;
        await using var writer = OtlpFileWriter.Start(new() { Dir = Path.Combine(output, "activity-telemetry-benchmark") });
        var emitter = new TelemetryEmitter(writer);
        timer.Restart();
        for (var i = 0; i < iterations; i++) PawnActivityTelemetry.Write(emitter, new(i + 1, sample, observed.End, "", observed.Outcome, 15));
        var enqueueMs = timer.Elapsed.TotalMilliseconds;
        await writer.ShutdownAsync(ct).ConfigureAwait(false);
        return new JsonObject { ["intervals"] = iterations, ["disabled_sink_ms"] = baselineMs,
            ["enabled_enqueue_ms"] = enqueueMs, ["enabled_flush_ms"] = timer.Elapsed.TotalMilliseconds,
            ["note"] = "Same captured native fixture interval; harness-process telemetry benchmark, not a whole-game TPS claim." };
    }
}
