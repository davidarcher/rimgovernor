using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/blight</c> (Go <c>blight.go</c>) is the blight responder vertical: a live game and a live service composed with
/// the blight family. The private test/blight_prepare fixture sows a small rice zone near the colonists and blights a few
/// plants. The controller's blight census (plant things in the planning window) must list exactly those plants
/// undesignated; the rounds open RemoveBlight on the census, the planner admits plans of CutPlant designations on the
/// blighted plants only, the colonist cuts them within a stall-bounded window, and the goal settles on the census
/// emptying, never on the receipt. After the service releases the game slot an independent native census confirms no
/// blighted plant stands.
/// </summary>
/// <remarks>
/// Adaptations: the service is the serve lifecycle of <see cref="ISession.ServeAsync"/> (the brain is in-process), so the
/// journal reads are the <see cref="IWatchStore"/> projections: one poll reads every plan (<c>PlanHistoryAsync("*")</c>) and
/// the RemoveBlight goal (<c>StandardSummaryAsync</c>, settled when its status reads <c>settled</c> and its need <c>met</c>)
/// instead of Go's per-plan waits; the checks on each plan (cut_plant rows only, 1 to 8 of them, fixture plants, each once)
/// are the same. The blight census comes from <see cref="StarterSiteHost.BlightSurveyAsync"/> (the controller's projection
/// is not buildable out of process).
/// </summary>
internal static class FarmBlight
{
    private const string PrepareOp = "test/blight_prepare";
    private const string CensusOp = "test/blight_census";

    internal static AcceptanceCase Case() => new()
    {
        Name = "farm/blight",
        Scope = "Native blight responder: the planning-window blight census drives the live rounder/planner to open "
            + "RemoveBlight and admit CutPlant designations on the blighted plants only; the colonists cut them within a "
            + "stall-bounded window and the standard settles on the census emptying, confirmed by an independent native read (#245).",
        Start = new FixtureStart { Op = PrepareOp, On = new LabStart() },
        Serve = new ServeSpec
        {
            Families = ["blight", "work", "field"],
            NativeTimeout = TimeSpan.FromSeconds(60),
            Prefix = "blight-accept",
        },
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
        Postmortem = PostmortemAsync,
    };

    /// <summary>Failure evidence: the same independent census a pass ends with.</summary>
    private static async Task PostmortemAsync(ISession s, CancellationToken cancellationToken)
    {
        if (s.Report.Has("blight_after"))
        {
            return;
        }

        try
        {
            s.Report["blight_postmortem"] = await s.Harness.CallAsync("blight-postmortem", CensusOp, new JsonObject(), cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            s.Report["blight_postmortem_error"] = e.Message;
        }
    }

    private sealed record Row(Cell Cell, string Zone, bool Designated);

    private sealed record Census(long Tick, SortedDictionary<string, Row> Rows)
    {
        public JsonObject Evidence() => new()
        {
            ["tick"] = Tick,
            ["plants"] = new JsonArray([.. Rows.Select(kv => (JsonNode?)new JsonObject
            {
                ["id"] = kv.Key,
                ["x"] = kv.Value.Cell.X,
                ["z"] = kv.Value.Cell.Z,
                ["zone"] = kv.Value.Zone,
                ["designated"] = kv.Value.Designated,
            })]),
        };
    }

    private static async Task<Census> ReadCensusAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        BlightSurvey survey;
        try
        {
            survey = await StarterSiteHost.BlightSurveyAsync(h, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }

        var rows = new SortedDictionary<string, Row>(StringComparer.Ordinal);
        foreach (var plant in survey.Plants)
        {
            rows[plant.ID] = new Row(plant.Cell, plant.Zone, plant.Designated);
        }

        return new Census(survey.Tick, rows);
    }

    private static bool Settled(JsonObject? goal) =>
        goal is not null
        && string.Equals(goal["status"].AsString(), "settled", StringComparison.OrdinalIgnoreCase)
        && string.Equals(goal["need"].AsString(), "met", StringComparison.OrdinalIgnoreCase);

    private static JsonArray Names(IEnumerable<string> values) => new([.. values.Order(StringComparer.Ordinal).Select(v => (JsonNode?)v)]);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var prepared = s.Prepared ?? [];
        var fixture = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in prepared["plants"].AsSlice())
        {
            fixture.Add((raw.AsMap() ?? [])["id"].AsString());
        }

        if (fixture.Count == 0)
        {
            throw new AcceptanceException($"fixture blighted no plants: {prepared.ToJson()}");
        }

        report["fixture_plants"] = Names(fixture);
        report["fixture_zone"] = prepared["zoneId"].Copy();
        report["fixture_cutter"] = prepared["cutter"].Copy();

        // Before: the census lists every fixture plant, undesignated, and nothing else blighted stands on the map.
        var before = await ReadCensusAsync(h, "blight-before", cancellationToken).ConfigureAwait(false);
        report["blight_before"] = before.Evidence();
        if (before.Rows.Count != fixture.Count)
        {
            throw new AcceptanceException($"blight-before: census lists {before.Rows.Count} blighted plants, the fixture blighted {fixture.Count}");
        }

        foreach (var (id, row) in before.Rows)
        {
            if (!fixture.Contains(id) || row.Designated)
            {
                throw new AcceptanceException($"blight-before: census row {id} designated={row.Designated} is not an undesignated fixture plant");
            }
        }

        // "work" rides along so the colony's work priorities match the controller's own assignment, which keeps plant
        // cutting enabled on the fixture's cutter.
        var observed = ObservedSession.Of(s);
        var service = await observed.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        IWatchStore? journal = null;
        var cut = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? settled = null;
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            journal = await service.StoreAsync(cancellationToken).ConfigureAwait(false);
            var store = journal;
            var plansSeen = 0;
            var zoneCreated = false;
            var wait = new Wait { Ceiling = TimeSpan.FromMinutes(5), Stall = Wait.StallBudget(), Terminal = service.Exited };
            var concern = PolicyFunctions.RemoveBlight.Value;

            // Each plan is CutPlant designations on fixture plants only, each plant once. Vanilla never cuts blight
            // undesignated, yet on the lab the other blighted plants leave the census within a few thousand ticks of the first
            // designation, so a plant can vanish before its own designation dispatches (an absent cancel, incidental); the
            // vertical is proven by at least one designation the executor observed through to completion and the goal
            // settling on the census, never on the receipt.
            try
            {
                await wait.WaitProgressAsync(
                    async ct =>
                    {
                        if (await store.RoundsRevisionAsync(ct).ConfigureAwait(false) == 0)
                        {
                            return (Wait.Signature("no-review"), false);
                        }

                        var plans = await store.PlanHistoryAsync(64, "*", ct).ConfigureAwait(false);
                        var batches = 0;
                        cut.Clear();
                        zoneCreated = false;
                        foreach (var plan in plans.Reverse<JsonObject>())
                        {
                            var rows = plan["progress"].AsSlice().Select(r => r.AsMap() ?? []).ToList();
                            if (rows.Any(r => r["kind"].AsString() == "zone_create" && r["stage"].AsString() == "completed"))
                            {
                                zoneCreated = true;
                            }

                            var cuts = rows.Where(r => r["kind"].AsString() == "cut_plant").ToList();
                            if (cuts.Count == 0)
                            {
                                continue;
                            }

                            if (cuts.Count != rows.Count || cuts.Count > 8)
                            {
                                throw new AcceptanceException($"blight plan {plan["plan"].AsString()} has {rows.Count} actions, {cuts.Count} of them cuts");
                            }

                            batches++;
                            var batch = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var row in cuts)
                            {
                                var plant = row["plant"].AsString();
                                if (!fixture.Contains(plant) || !batch.Add(plant))
                                {
                                    throw new AcceptanceException($"cut of {plant}: not a unique fixture plant");
                                }

                                seen.Add(plant);
                                if (row["stage"].AsString() == "completed" && row["receipt"].AsString().Length > 0)
                                {
                                    if (!cut.Add(plant))
                                    {
                                        throw new AcceptanceException($"cut of {plant}: already cut by an earlier plan");
                                    }

                                    report["blight_completed_tick"] = row["tick"].AsNumber();
                                }
                            }
                        }

                        plansSeen = batches;
                        var goal = await store.StandardSummaryAsync(concern, ct).ConfigureAwait(false);
                        settled = goal;
                        var done = Settled(goal) && cut.Count > 0 && zoneCreated;
                        return (Wait.Signature(batches, cut.Count, zoneCreated, goal?["status"].AsString(), goal?["need"].AsString()), done);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (WaitError e)
            {
                throw new AcceptanceException(
                    $"RemoveBlight neither settled on the emptied census with a completed cut nor was followed by a second field (plans={plansSeen} cut={cut.Count} second_field={zoneCreated} goal={settled.ToJson()}): {e.Message}",
                    e);
            }

            report["concern_id"] = concern;
            report["blight_plans"] = plansSeen;
            report["plants_cut"] = cut.Count;
            if (cut.Count == 0)
            {
                throw new AcceptanceException($"no cut designation completed under the executor across {plansSeen} plans");
            }

            report["blight_standard"] = settled.Copy();
            report["plants_designated"] = Names(seen);
        }
        catch
        {
            (journal as IDisposable)?.Dispose();
            _ = await service.StopAsync().ConfigureAwait(false);
            throw;
        }

        (journal as IDisposable)?.Dispose();
        report["service"] = await service.StopAsync().ConfigureAwait(false);
        h = await observed.ReattachAsync(cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);

        // Ordinary growers must sow the new zone; its creation receipt is not proof of planting. Advance a bounded window
        // before the native audit, in steps, each with its own advance wall timeout (120 s by default): a Superfast
        // advance under box load (338 tps) took about 35 s for 12000 ticks.
        for (var i = 0; i < 4; i++)
        {
            _ = await s.AdvanceAsync(3000, null, cancellationToken).ConfigureAwait(false);
        }

        var native = await h.CallAsync("blight-after-native", CensusOp, new JsonObject(), cancellationToken).ConfigureAwait(false);
        report["blight_after_native"] = native;
        SeparatedPlantedField(native, prepared);
        var standing = native["blighted"].AsSlice();
        if (standing.Count != 0)
        {
            throw new AcceptanceException($"blight-after: {standing.Count} blighted plants still stand natively: {native["blighted"].ToJson()}");
        }

        var after = await ReadCensusAsync(h, "blight-after", cancellationToken).ConfigureAwait(false);
        report["blight_after"] = after.Evidence();
        if (after.Rows.Count != 0)
        {
            throw new AcceptanceException($"blight-after: census still lists {after.Rows.Count} plants");
        }
    }

    private static Cell CellOf(JsonNode? raw)
    {
        var c = raw.AsMap() ?? [];
        return new Cell((int)c["x"].AsNumber(), (int)c["z"].AsNumber());
    }

    /// <summary>The new field is a separate zone (no shared edge with the original) and holds a natively sown crop.</summary>
    private static void SeparatedPlantedField(JsonObject native, JsonObject prepared)
    {
        var old = new HashSet<Cell>(prepared["zoneCells"].AsSlice().Select(CellOf));
        if (old.Count == 0)
        {
            throw new AcceptanceException("fixture omitted field cells");
        }

        var planted = false;
        foreach (var raw in native["zones"].AsSlice())
        {
            var zone = raw.AsMap() ?? [];
            if (zone["id"].AsNumber() == prepared["zoneId"].AsNumber())
            {
                continue;
            }

            foreach (var cell in zone["cells"].AsSlice().Select(CellOf))
            {
                foreach (var n in new[] { new Cell(cell.X - 1, cell.Z), new Cell(cell.X + 1, cell.Z), new Cell(cell.X, cell.Z - 1), new Cell(cell.X, cell.Z + 1) })
                {
                    if (old.Contains(n))
                    {
                        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"second field at {cell} shares an edge with the original field"));
                    }
                }
            }

            planted = planted || zone["planted"].AsNumber() > 0;
        }

        if (!planted)
        {
            throw new AcceptanceException("no separate second field contains a natively sown crop");
        }
    }
}
