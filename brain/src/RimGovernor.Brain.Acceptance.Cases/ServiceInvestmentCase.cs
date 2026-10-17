using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Telemetry.Otlp;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class ServiceInvestmentCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "shelter/service-investment",
        Scope = "Prepared cold occupied room and two partly funded frames competing for five steel enter ordinary review. The shared selector must finish useful heat through journal/Hands while leaving unrelated lower-tier construction in place. Prepared layout describes existing room geometry, never a chosen worker or task. Pure tests cannot prove native frame work, power or warming.",
        Start = new LabStart(),
        RequiredOps = ["test/service_shelter_prepare", "test/service_shelter_read"],
        Keep = ["Rest"], QuietWorld = true, Crew = new Crew { Size = 2 }, Service = true,
        NoCheckpoint = true, Budget = TimeSpan.FromMinutes(5), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            // Recreate only the fixture's already-standing room description in the existing save owner.
            var prepared = await s.Harness.CallAsync("prepare-service-room", "test/service_shelter_prepare", [], ct).ConfigureAwait(false);
            var x = (int)prepared["x"].AsNumber(); var z = (int)prepared["z"].AsNumber();
            var plan = new LayoutPlan { Cold = true, NextRoomId = 2, Rooms = [new PlannedRoom
            { Id = 1, Role = PlannedRole.Shelter, Region = RectRegion(new(x, z, 3, 3)),
                Door = new((int)prepared["doorX"].AsNumber(), (int)prepared["doorZ"].AsNumber()), DoorRot = Rotation.East }] };
            var loaded = Harness.Outcome(await s.Harness.WireAsync("read-service-layout", "lifecycle_read_governor_state", new JsonObject(), ct).ConfigureAwait(false), "loaded").Value;
            var blobs = new JsonObject();
            foreach (var (key, value) in loaded["blobs"].AsMap() ?? []) blobs[key] = value.AsString();
            blobs[Store.Store.GovernorLayoutPlanKey] = new JsonObject
            {
                ["schemaVersion"] = Store.Store.GovernorStateSchemaVersion,
                ["scope"] = new JsonObject { ["colony"] = s.Identity["colonyId"].AsString(), ["map"] = s.Identity["mapId"].AsNumber(), ["tick"] = 0 },
                ["record"] = JsonNode.Parse(StoreJson.Serialize(plan))
            }.ToJsonString();
            _ = Harness.Outcome(await s.Harness.WireAsync("put-service-layout", "lifecycle_put_governor_state_batch", new JsonObject { ["blobs"] = blobs }, ct).ConfigureAwait(false), "loaded");
            var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var run = await BrainRun.StartAsync(s, null, "service-investment", "service-investment", ct).ConfigureAwait(false);
            try
            {
                using var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), ct).ConfigureAwait(false);
                var witnessed = new HashSet<string>(StringComparer.Ordinal);
                await new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited }.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("service-investment-observe", "test/service_shelter_read", [], token).ConfigureAwait(false);
                    foreach (var method in await ServiceJournal.NeedMethodsAsync(journal, Policy.AdminFamily.WorkAssignments, token).ConfigureAwait(false))
                        witnessed.Add(method);
                    var methods = read["methods"]!.AsArray().Select(n => n.AsString()).ToArray();
                    var logs = Path.Combine(GameSessions.EndpointDirectory(s.Config.Configuration, s.Config.GameId), "logs.jsonl");
                    var explained = OtlpTimeline.Read(logs).Any(row => row.WallTime >= started && row.Kind == "coupled_selection"
                        && row.Payload.TryGetValue("target", out var target) && target is string method && methods.Contains(method, StringComparer.Ordinal)
                        && row.Payload.TryGetValue("attrs", out var attrs) && attrs is IReadOnlyDictionary<string, object?> attributes
                        && attributes.TryGetValue("service_investments", out var investments) && investments is IReadOnlyCollection<object?> { Count: > 0 });
                    s.Report["investment"] = read; s.Report["explained"] = explained;
                    if (!read["sparePresent"].Flag() || read["spareRemainingWork"].AsNumber() < 99)
                        throw new AcceptanceException("Unrelated adopted construction was completed or cancelled: " + read.ToJson());
                    var complete = methods.Length > 0 && methods.All(witnessed.Contains) && explained && read["worked"].Flag()
                        && read["completed"].Flag() && read["powered"].Flag() && read["temperature"].AsNumber() > 0;
                    if (!complete && read["elapsed"].AsNumber() >= 12000)
                        throw new AcceptanceException("Useful shelter investment did not complete and warm within 12000 ticks: " + read.ToJson());
                    return (Wait.Signature(read["tick"].AsNumber()), complete);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        }
    };
}
