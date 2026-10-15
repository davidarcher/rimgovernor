using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class PortfolioNeedsCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/coupled-needs",
        Scope = "Prepared hunger, fatigue and ripe rice enter ordinary review, shared selection, Method journal and Hands. Observe harvested output rebound to real stacks, completed ingestion and sleep/wake/recreation despite a legacy Work timetable, then introduce new observed hunger and independent food lots for the crew and require a second Method and real ingestion. No chosen task sequence is installed. Pure tests cannot prove real job completion, output identity or live authority.",
        Start = new FixtureStart { Op = "test/portfolio_needs_prepare", On = new LabStart() },
        RequiredOps = ["test/portfolio_needs_prepare", "test/portfolio_needs_read", "test/portfolio_needs_repeat"],
        Keep = ["Food", "Rest", "Joy"], QuietWorld = true, Crew = new Crew { Size = 3 },
        Service = true, NoCheckpoint = true, Budget = TimeSpan.FromMinutes(8), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var run = await BrainRun.StartAsync(s, null, "coupled-needs", "coupled-needs", ct).ConfigureAwait(false);
            try
            {
                using var store = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), ct).ConfigureAwait(false);
                var repeated = false; var firstCycleMethods = 0; var repeatStart = 0d;
                var witnessedMethods = new HashSet<string>(StringComparer.Ordinal);
                var wait = new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited };
                await wait.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("portfolio-observe", "test/portfolio_needs_read", [], token).ConfigureAwait(false);
                    foreach (var owner in await store.OpenStandardsAsync(PolicyFunctions.EnsureWorkAssignments, token).ConfigureAwait(false))
                        foreach (var method in await store.LoadMethodsAsync(owner.Standard.Id, owner.Standard.Episode, token).ConfigureAwait(false))
                            if (method.MethodId.Value.StartsWith("coupled-", StringComparison.Ordinal)) witnessedMethods.Add(method.MethodId.Value);
                    var applied = read["methods"]!.AsArray().Select(n => n.AsString()).ToArray();
                    var journaled = applied.Length > 0 && applied.All(witnessedMethods.Contains);
                    s.Report["portfolio"] = read;
                    s.Report["journaled"] = journaled;
                    var logs = Path.Combine(GameSessions.EndpointDirectory(s.Config.Configuration, s.Config.GameId), "logs.jsonl");
                    var tick = read["tick"].AsNumber();
                    var evidence = OtlpTimeline.Read(logs).Where(row => row.WallTime >= started
                        && row.Payload.TryGetValue("target", out var target) && target is string method
                        && applied.Contains(method, StringComparer.Ordinal) && witnessedMethods.Contains(method)
                        && row.Payload.TryGetValue("attrs", out var attributes) && attributes is IReadOnlyDictionary<string, object?> attrs
                        && attrs.TryGetValue("observed_tick", out var observed) && observed is double observedTick
                        && observedTick >= tick - read["elapsed"].AsNumber() && observedTick <= tick)
                        .Select(row => (row.Kind, Attrs: (IReadOnlyDictionary<string, object?>)row.Payload["attrs"]!)).ToArray();
                    var selectedEvidence = evidence.Any(row => row.Kind == "coupled_selection"
                        && row.Attrs.TryGetValue("selected_ids", out var ids) && ids is IReadOnlyCollection<object?> { Count: > 0 });
                    var investmentEvidence = evidence.Any(row => row.Kind == "coupled_selection"
                        && row.Attrs.TryGetValue("service_investments", out var investments) && investments is IReadOnlyCollection<object?> { Count: > 0 });
                    var observedEvidence = evidence.Any(row => row.Kind == "coupled_observed"
                        && (row.Attrs.TryGetValue("satisfied_tasks", out var satisfied) && satisfied is IReadOnlyCollection<object?> { Count: > 0 }
                            || row.Attrs.TryGetValue("native_states", out var states) && states is IReadOnlyCollection<object?> values
                                && values.OfType<string>().Any(state => state.EndsWith(":completed", StringComparison.Ordinal))));
                    s.Report["selectionEvidence"] = selectedEvidence;
                    s.Report["investmentEvidence"] = investmentEvidence;
                    s.Report["observedEvidence"] = observedEvidence;
                    var complete = investmentEvidence && journaled && selectedEvidence && observedEvidence && read["rebound"].Flag() && read["ate"].Flag() && read["slept"].Flag() && read["woke"].Flag() && read["played"].Flag();
                    if (complete && !repeated)
                    {
                        s.Report["firstCycle"] = read.DeepClone();
                        firstCycleMethods = applied.Length;
                        var repeat = await s.Harness.CallAsync("repeat-needs", "test/portfolio_needs_repeat", [], token).ConfigureAwait(false);
                        s.Report["repeat"] = repeat;
                        repeatStart = repeat["tick"].AsNumber();
                        repeated = true;
                        return (Wait.Signature(read["tick"].AsNumber()), false);
                    }
                    complete &= repeated && applied.Length > firstCycleMethods;
                    // The second demand begins after observed first-cycle recovery. Its finite
                    // window includes finishing the already-authorized recreation commitment.
                    var phaseElapsed = repeated ? tick - repeatStart : read["elapsed"].AsNumber();
                    // A full-rest alternative from the prepared fatigue takes about 20,000 ticks.
                    // Allow it and subsequent recreation without requiring a particular sleep target.
                    var phaseBudget = repeated ? 6000 : 30000;
                    var phase = repeated ? "repeat ingestion" : "food, bounded sleep and recreation";
                    if (!complete && phaseElapsed >= phaseBudget)
                        throw new AcceptanceException($"Normal coupled planner did not complete {phase} within {phaseBudget} phase ticks: " + read.ToJson());
                    return (Wait.Signature(read["tick"].AsNumber()), complete);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        },
    };
}
