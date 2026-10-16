using RimGovernor.Brain.Domain;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Prepared support demand enters normal review; no chosen Method or pawn sequence is installed.</summary>
internal static class SupportPortfolioCase
{
    internal static AcceptanceCase Create(string name, string scenario, string claim) => new()
    {
        Name = name, Scope = claim + " Native physical completion and participant occupancy cannot be established by a pure forecast test.",
        Start = new FixtureStart { Op = "test/support_" + scenario + "_prepare", On = new LabStart() },
        RequiredOps = ["test/support_" + scenario + "_prepare", "test/support_read", "test/support_cancel"],
        Keep = ["Food"], QuietWorld = true, Crew = new Crew { Size = 3 }, Service = true, NoCheckpoint = true,
        Budget = TimeSpan.FromMinutes(5), Stall = TimeSpan.FromSeconds(45),
        Run = (session, ct) => RunAsync(session, scenario, ct),
    };

    private static async Task RunAsync(ISession session, string scenario, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var run = await BrainRun.StartAsync(session, null, "support-" + scenario, "support-" + scenario, ct).ConfigureAwait(false);
        try
        {
            using var store = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(session.Config), ct).ConfigureAwait(false);
            var witnessed = new HashSet<string>(StringComparer.Ordinal);
            var cancelled = false;
            await new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited }.WaitProgressAsync(async token =>
            {
                var read = await session.Harness.CallAsync("support-observe", "test/support_read", [], token).ConfigureAwait(false);
                if (!read["success"].Flag()) throw new AcceptanceException("Support observation refused: " + read.ToJson());
                foreach (var owner in await store.OpenStandardsAsync(PolicyFunctions.EnsureWorkAssignments, token).ConfigureAwait(false))
                    foreach (var method in await store.LoadMethodsAsync(owner.Standard.Id, owner.Standard.Episode, token).ConfigureAwait(false))
                        witnessed.Add(method.MethodId.Value);
                var applied = read["methods"]!.AsArray().Select(n => n.AsString()).ToArray();
                var journaled = applied.Length > 0 && applied.All(witnessed.Contains);
                if (scenario == "cancel" && !cancelled && read["inTransit"].Flag())
                {
                    var reply = await session.Harness.CallAsync("withdraw-support-demand", "test/support_cancel", [], token).ConfigureAwait(false);
                    if (!reply["success"].Flag()) throw new AcceptanceException("Demand cancellation refused: " + reply.ToJson());
                    cancelled = true;
                }
                var logs = Path.Combine(GameSessions.EndpointDirectory(session.Config.Configuration, session.Config.GameId), "logs.jsonl");
                var rows = OtlpTimeline.Read(logs).Where(row => row.WallTime >= started
                    && row.Payload.TryGetValue("target", out var target) && target is string id && applied.Contains(id, StringComparer.Ordinal)
                    && witnessed.Contains(id)
                    && row.Payload.TryGetValue("attrs", out var value) && value is IReadOnlyDictionary<string, object?> attrs
                    && attrs.TryGetValue("observed_tick", out var observedTick) && observedTick is double tick
                    && tick >= read["tick"].AsNumber() - read["elapsed"].AsNumber() && tick <= read["tick"].AsNumber()).ToArray();
                var selected = rows.Any(row => row.Kind == "coupled_selection"
                    && ((IReadOnlyDictionary<string, object?>)row.Payload["attrs"]!).TryGetValue("selected_ids", out var ids)
                    && ids is IReadOnlyCollection<object?> { Count: > 0 });
                var observed = rows.Any(row => row.Kind == "coupled_observed"
                    && (((IReadOnlyDictionary<string, object?>)row.Payload["attrs"]!).TryGetValue("satisfied_tasks", out var satisfied)
                        && satisfied is IReadOnlyCollection<object?> { Count: > 0 }
                        || ((IReadOnlyDictionary<string, object?>)row.Payload["attrs"]!).TryGetValue("native_states", out var states)
                        && states is IReadOnlyCollection<object?> values && (scenario == "cancel" ? values.Count > 0 : values.OfType<string>().Any(state => state.EndsWith(":completed", StringComparison.Ordinal)))));
                session.Report["support"] = read;
                session.Report["journaled"] = journaled;
                session.Report["selectedEvidence"] = selected;
                session.Report["observedEvidence"] = observed;
                var criticalCare = scenario != "care" || await CriticalCareEvidenceAsync(store, read, token).ConfigureAwait(false);
                session.Report["criticalCareJournalEvidence"] = criticalCare;
                var outcome = scenario switch
                {
                    "bench" => read["cleaned"].Flag() && read["produced"].Flag(),
                    "care" or "routine" => criticalCare && read["tended"].Flag() && read["careWithinServiceBudget"].Flag() && read["cleaned"].Flag(),
                    "delivery" => read["delivered"].Flag() && read["produced"].Flag() && read["quantityBounded"].Flag(),
                    "cancel" => cancelled && read["cancelled"].Flag() && read["released"].Flag(),
                    "chunks" => read["usefulPickedUp"].Flag() && read["delivered"].Flag() && read["quantityBounded"].Flag() && read["required"].AsNumber() > 0 && read["unrelatedUnmoved"].Flag(),
                    _ => false,
                };
                var complete = outcome && journaled && selected && observed && read["participantAccounting"].Flag();
                if (!complete && read["elapsed"].AsNumber() >= 12000)
                    throw new AcceptanceException("Normal support portfolio did not establish its physical outcome within 12000 ticks: " + read.ToJson());
                return (Wait.Signature(read["tick"].AsNumber()), complete);
            }, ct).ConfigureAwait(false);
        }
        finally { await run.StopAsync(ct).ConfigureAwait(false); }
    }
    private static async Task<bool> CriticalCareEvidenceAsync(RimGovernor.Brain.Store.Store store, JsonObject read, CancellationToken ct)
    {
        var doctors = read["criticalDoctors"]!.AsArray().Select(n => n.AsString()).ToHashSet(StringComparer.Ordinal);
        if (doctors.Count == 0) return false;
        var review = await store.LoadRoundsAsync(ct).ConfigureAwait(false);
        // The patient's critical Need owns its tends (#3258): every attempt in this world, retired ones included.
        var need = PolicyFunctions.CriticalNeed(new PawnId(read["patient"].AsString()));
        var history = await Task.Run(() => store.OwnerMethodHistory(store.LoadNeed(need, review.Snapshot, ct), ct), ct).ConfigureAwait(false);
        foreach (var method in history)
        {
            var plan = await store.LoadPlanAsync(method.Plan, ct).ConfigureAwait(false);
            for (var i = 0; i < plan.Spec.Actions.Count; i++)
                if (plan.Spec.Actions[i].Tend is { } tend && tend.Patient.Value == read["patient"].AsString()
                    && doctors.Contains(tend.Doctor.Value) && plan.Progress[i].View.Stage == Stage.Completed) return true;
        }
        return false;
    }
}
