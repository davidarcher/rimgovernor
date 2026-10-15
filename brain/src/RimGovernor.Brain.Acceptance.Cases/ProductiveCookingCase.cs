using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class ProductiveCookingCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/productive-cooking",
        Scope = "Prepared useful simple-meal demand enters normal selection, journal and Hands. Native reads compare all per-level cooking rates against actual native stats without changing live observations; observe productive learning, physical meal output and subsequent work. Unit tests prove allocation tradeoffs; this case proves native mechanics and execution without a fixture-selected winner.",
        Start = new FixtureStart { Op = "test/productive_cooking_prepare", On = new LabStart() },
        RequiredOps = ["test/productive_cooking_prepare", "test/productive_cooking_read"],
        Keep = ["Food"], QuietWorld = true, Crew = new Crew { Size = 3 }, Service = true,
        NoCheckpoint = true, Budget = TimeSpan.FromMinutes(5), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var run = await BrainRun.StartAsync(s, null, "productive-cooking", "productive-cooking", ct).ConfigureAwait(false);
            try
            {
                using var store = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), ct).ConfigureAwait(false);
                var journaled = new HashSet<string>(StringComparer.Ordinal);
                await new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited }.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("productive-cooking", "test/productive_cooking_read", [], token).ConfigureAwait(false);
                    foreach (var owner in await store.OpenStandardsAsync(PolicyFunctions.EnsureWorkAssignments, token).ConfigureAwait(false))
                        foreach (var method in await store.LoadMethodsAsync(owner.Standard.Id, owner.Standard.Episode, token).ConfigureAwait(false))
                            journaled.Add(method.MethodId.Value);
                    var methods = read["methods"]!.AsArray().Select(n => n.AsString()).ToArray();
                    var logs = Path.Combine(GameSessions.EndpointDirectory(s.Config.Configuration, s.Config.GameId), "logs.jsonl");
                    var evidence = OtlpTimeline.Read(logs).Any(row => row.WallTime >= started && row.Kind == "coupled_selection"
                        && row.Payload.TryGetValue("target", out var target) && target is string method && methods.Contains(method)
                        && row.Payload.TryGetValue("attrs", out var attributes) && attributes is IReadOnlyDictionary<string, object?> attrs
                        && attrs.TryGetValue("predicted_tasks", out var tasks) && tasks is IEnumerable<object?> values
                        && values.OfType<IReadOnlyDictionary<string, object?>>().Any(task => task.TryGetValue("productive_learning", out var learning) && learning is not null));
                    s.Report["observed"] = read; s.Report["predictedLearningEvidence"] = evidence;
                    if (read["learningFailures"]!.AsArray().Count != 0) throw new AcceptanceException("Native learning differential failed: " + read.ToJson());
                    var complete = methods.Length > 0 && methods.All(journaled.Contains) && evidence && read["output"].Flag()
                        && read["subsequentWork"].Flag() && read["completions"].AsNumber() >= 2
                        && read["skills"]!.AsArray().Any(p => p!["level"].AsNumber() >= 6);
                    if (!complete && read["elapsed"].AsNumber() >= 18000)
                        throw new AcceptanceException("Normal planner did not complete useful cooking and subsequent work within 18000 ticks: " + read.ToJson());
                    return (Wait.Signature(read["tick"].AsNumber()), complete);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        }
    };
}
