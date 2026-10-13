using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clearance/shrine-breach</c> (Go <c>shrine_breach.go</c>): a sealed shrine breach, the ActiveCombat handoff and
/// observed recovery; empty casket claim and default never-open protection. ShrineFixture is compiled by
/// <c>setup -rebuild -fixture ShrineFixture</c>. clearance/shrine-claim replays as a colony snapshot instead
/// (internal/snapshot), so only the non-claim run registers.
/// </summary>
internal static class ClearanceShrineBreach
{
    private const string Stage = "sealed-shrine-ready";
    private const string FixtureKey = "sealed_shrine_fixture";

    /// <summary>The shrine planner's claim, breach and open methods (plans are found by method, not plan id).</summary>
    private static readonly string[] ShrinePlanMethods = ["claim-*", "breach-*", "open-*"];

    private static readonly string[] DeconstructMethods = ["deconstruct-*"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "clearance/shrine-breach",
        Scope = "Sealed shrine breach, ActiveCombat handoff and observed recovery; empty casket claim and default never-open protection.",
        Start = new SaveStart { Name = "RimGovernor-tribal8-baseline" },
        RequiredOps = ["test/shrine_prepare", "test/shrine_audit"],

        // The repair family composes because the fight damages buildings: the breach waits on repairs
        // (ClearAncientShrine is method_unavailable while the repairs latch holds), so without it recovery never comes.
        Serve = new ServeSpec { Families = ["shrine", "defense", "clearance", "repair", "tend", "rescue"], Prefix = "shrine-breach" },
        Stages = [Stage],
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 3 },
        Stall = TimeSpan.FromSeconds(90),
        Run = (s, ct) => RunAsync(s, claim: false, ct),
    };

    private static async Task RunAsync(ISession s, bool claim, CancellationToken cancellationToken)
    {
        JsonObject? fixture = null;
        await s.StageAsync(
            Stage,
            async ct =>
            {
                fixture = await s.Harness.CallAsync("prepare-sealed-shrine", "test/shrine_prepare", new JsonObject { ["sealedBreach"] = true }, ct).ConfigureAwait(false);
                Checkpoints.SetState(FixtureKey, fixture.DeepClone());
            },
            cancellationToken).ConfigureAwait(false);
        if (StagePlanning.RestoredState(s.Resumed, s.Staged, FixtureKey).AsMap() is { } restored)
        {
            fixture = restored;
        }

        fixture ??= [];
        s.Report["fixture"] = fixture.DeepClone();
        var caskets = fixture["caskets"].AsSlice().Select(raw => raw.AsString()).ToList();
        if (caskets.Count != 2 || !Flag(fixture["properRoom"]) || fixture["guard"].AsString().Length == 0 || fixture["breach"].AsString().Length == 0)
        {
            throw new AcceptanceException($"invalid sealed shrine fixture: {fixture.ToJson()}");
        }

        async Task<JsonObject> Audit(string label)
        {
            var args = new JsonObject
            {
                ["caskets"] = string.Join(';', caskets),
                ["guard"] = fixture["guard"]?.DeepClone(),
                ["breach"] = fixture["breach"]?.DeepClone(),
                ["salvage"] = fixture["salvage"]?.DeepClone(),
            };
            var result = await s.Harness.CallAsync(label, "test/shrine_audit", args, cancellationToken).ConfigureAwait(false);
            s.Report[label] = result.DeepClone();
            return result;
        }

        var before = await Audit("sealed_before").ConfigureAwait(false);
        if (!Flag(before["breachPresent"]) || !Flag(before["guardPresent"]) || Flag(before["guardDead"]))
        {
            throw new AcceptanceException($"fixture already breached: {before.ToJson()}");
        }

        if (before["caskets"].AsSlice().Count != 2)
        {
            throw new AcceptanceException($"fixture must contain exactly two caskets: {before.ToJson()}");
        }

        foreach (var raw in before["caskets"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (!Flag(row["fogged"]) || Flag(row["playerOwned"]) || Flag(row["hasContents"]) != (row["id"].AsString() == caskets[1]))
            {
                throw new AcceptanceException($"fixture caskets must be fogged, unowned, one empty and one filled: {row.ToJson()}");
            }
        }

        var observed = await TradeKit.ObserveAsync(s.Harness, "sealed_shrine_census", "observations_get_ancient_shrines", s.Identity, null, cancellationToken).ConfigureAwait(false);
        s.Report["sealed_shrine_census"] = observed.DeepClone();
        CheckSealedBreachWall(observed, fixture["breach"].AsString());

        var service = await ClearanceKit.StartAsync(s, cancellationToken).ConfigureAwait(false);
        var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false);
        try
        {
            bool drafted = false, breached = false, combat = false, claimed = false, salvaged = false, recovered = false;
            var wait = new Wait
            {
                Ceiling = TimeSpan.FromMinutes(6),
                Stall = TimeSpan.FromSeconds(90),
                Interval = TimeSpan.FromSeconds(1),
                Terminal = service.Exited,
            };
            await wait.WaitProgressAsync(
                async ct =>
                {
                    var holds = await journal.ShrineHoldsAsync(ct).ConfigureAwait(false);
                    s.Report["shrine_holds"] = new JsonArray([.. holds.Select(h => (JsonNode?)h)]);

                    var incident = await journal.SampleStandardAsync(PolicyFunctions.ActiveCombat.Value, ct).ConfigureAwait(false);
                    if (Flag(incident["concern_bound"]) && Num(incident["method_count"]) > 0)
                    {
                        combat = true;
                        s.Report["combat_incident"] = incident.DeepClone();
                    }

                    var project = await journal.SampleStandardAsync(PolicyFunctions.ClearAncientShrine.Value, ct).ConfigureAwait(false);
                    if (Flag(project["concern_bound"]))
                    {
                        recovered = string.Equals(project["status"].AsString(), "settled", StringComparison.OrdinalIgnoreCase);
                        s.Report["shrine_project"] = project.DeepClone();
                    }

                    var states = new List<string>();
                    foreach (var patterns in new[] { ShrinePlanMethods, DeconstructMethods })
                    {
                        var plans = await journal.PlanHistoryAsync(256, patterns, ct).ConfigureAwait(false);
                        foreach (var plan in plans)
                        {
                            foreach (var step in plan["steps"].AsSlice())
                            {
                                var row = step.AsMap() ?? [];
                                var kind = row["kind"].AsString();
                                var stage = row["stage"].AsString();
                                var action = row["action"].AsMap() ?? [];
                                states.Add(row["id"].AsString() + ":" + stage);
                                if (kind == Wire.Name(ActionKind.OpenCasket) && caskets.Contains(action["casket"].AsString()))
                                {
                                    throw new AcceptanceException($"default policy issued OpenCasket: {row["id"].AsString()}");
                                }

                                if (stage != "completed")
                                {
                                    continue;
                                }

                                drafted = drafted || (kind == Wire.Name(ActionKind.OwnedDraft) && ReferenceEquals(patterns, ShrinePlanMethods));
                                if (kind == Wire.Name(ActionKind.Deconstruction))
                                {
                                    breached = breached || action["target"].AsString() == fixture["breach"].AsString();
                                    salvaged = salvaged || action["target"].AsString() == fixture["salvage"].AsString();
                                }

                                claimed = claimed || (kind == Wire.Name(ActionKind.ClaimBuilding) && action["thing"].AsString() == caskets[0]);
                            }
                        }
                    }

                    s.Report["action_states"] = new JsonArray([.. states.Select(x => (JsonNode?)x)]);
                    s.Report["milestones"] = new JsonObject
                    {
                        ["drafted"] = drafted,
                        ["breached"] = breached,
                        ["active_combat"] = combat,
                        ["claimed"] = claimed,
                        ["salvaged"] = salvaged,
                        ["recovery_observed"] = recovered,
                    };
                    var signature = Wait.Signature(string.Join(',', states), drafted, breached, combat, claimed, salvaged, recovered, string.Join(',', holds));
                    return (signature, drafted && breached && combat && recovered && (!claim || (claimed && salvaged)));
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            journal.Dispose();
        }

        _ = await service.StopAsync().ConfigureAwait(false);
        await ClearanceKit.ReattachAsync(s, cancellationToken).ConfigureAwait(false);
        var after = await Audit("sealed_after").ConfigureAwait(false);
        CheckShrineBreach(after, caskets, claim);
    }

    private static void CheckSealedBreachWall(JsonObject observed, string wall)
    {
        foreach (var raw in observed["shrines"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            foreach (var rawWall in row["breachWalls"].AsSlice())
            {
                if ((rawWall.AsMap() ?? [])["entityId"].AsString() != wall)
                {
                    continue;
                }

                if (!Flag(row["sealed"]) || Flag(row["guardsKnown"]) || row["guards"].AsSlice().Count != 0 || row["caskets"].AsSlice().Count != 0)
                {
                    throw new AcceptanceException($"sealed breach census exposed the interior: {row.ToJson()}");
                }

                return;
            }
        }

        throw new AcceptanceException($"sealed shrine census omitted fixture breach wall {wall}: {observed.ToJson()}");
    }

    private static void CheckShrineBreach(JsonObject after, List<string> caskets, bool claim)
    {
        if (!Flag(after["guardDead"]) || Flag(after["breachPresent"]) || Num(after["colonistsDead"]) != 0)
        {
            throw new AcceptanceException($"breach must kill guard and spare every colonist: {after.ToJson()}");
        }

        var rows = after["caskets"].AsSlice();
        if (rows.Count != 2)
        {
            throw new AcceptanceException($"both caskets must survive: {after.ToJson()}");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rows)
        {
            var row = raw.AsMap() ?? [];
            var id = row["id"].AsString();
            if (!caskets.Contains(id) || seen.Contains(id) || Flag(row["designated"]) || Flag(row["hasContents"]) != (id == caskets[1]))
            {
                throw new AcceptanceException($"casket contents or protection changed: {row.ToJson()}");
            }

            _ = seen.Add(id);
            if (claim && id == caskets[0] && !Flag(row["playerOwned"]))
            {
                throw new AcceptanceException($"empty casket not claimed: {row.ToJson()}");
            }
        }

        if (claim && Flag(after["salvagePresent"]))
        {
            throw new AcceptanceException($"clearance salvage remains: {after.ToJson()}");
        }
    }
}
