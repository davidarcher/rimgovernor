using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/rich-soil</c> (Go <c>rich_soil.go</c>): a fresh colony on the baseline world, whose map holds rich soil
/// beside the centre, derives its own layout plan. The audit reads the plan from the journal, the map survey and the
/// growing zones natively: no planned room or hallway on rich soil, each fertile patch one field zone wholly inside or
/// outside the traced ring (crossed only where the ring runs along the edge margin line), and the crop zones inside a
/// patch one block with no gap. A colonist is then spawned and the plan must grow (a layout_plan replanned row for the
/// new count) within one in-game hour.
/// </summary>
internal static class LayoutRichSoil
{
    /// <summary>The hourly layout trigger's bound.</summary>
    private const long ReplanWithin = 2500;

    internal static AcceptanceCase Case() => new()
    {
        Name = "layout/rich-soil",
        Scope = "Issue #1291: on the tribal " + Baselines.BaselineName + " world (rich soil near the centre) a fresh colony's own layout plan " +
            "puts no room or hallway on rich soil, keeps each fertile patch one field zone wholly inside or outside the traced ring " +
            "(split only where the ring runs along the edge margin line), zones adjacent crop blocks with no gap, and grows the plan " +
            "within 2500 ticks of a colonist joining. A snapshot test cannot cover it end to end: the crop zones are native, and the " +
            "replan is the live hourly trigger (the plan assertions alone replay offline in TestRichSoilBaselinePlan).",
        Start = new SaveStart { Name = Baselines.BaselineName },
        RequiredOps = [DebugGame.LabSpawnTool],
        Keep = [NeedDef.Food.Value],
        Serve = new ServeSpec
        {
            Families = ["shelter", "expansion", "field"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "layout-rich-soil",
        },
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 3 },
        Reason = "two short serves: a plan and its first field review, then one in-game hour after a colonist joins",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var plan = await ServePlannedAsync(s, report, cancellationToken).ConfigureAwait(false);
        var h = s.Reattach();
        _ = await h.CallAsync("pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var surveyed = await LayoutSiteHost.SurveyAsync(h, cancellationToken).ConfigureAwait(false);
        var crops = await CropZonesAsync(s, h, cancellationToken).ConfigureAwait(false);
        var audit = LayoutSoilAudit.Audit(plan, surveyed.Survey, crops);
        report["soil"] = audit.ToJson();
        report["rich_overlap"] = audit.RichOverlapText();
        if (audit.Error() is { } failed)
        {
            throw new AcceptanceException(failed);
        }

        if (audit.RichCells == 0 || audit.Patches == 0)
        {
            throw new AcceptanceException($"the world has no rich soil or fertile patch to audit: {audit.ToJson().ToJson()}");
        }

        if (audit.CropZones == 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no crop zone was laid by tick {surveyed.Tick}"));
        }

        var pawns = surveyed.Colonists;
        var at = plan.Anchor(PolicyFunctions.PlannedBedroom, null)
            ?? throw new AcceptanceException("the plan holds no bedroom to spawn the colonist in");
        var spawned = await h.CallAsync("spawn-colonist", DebugGame.LabSpawnTool, new JsonObject { ["def"] = "Colonist", ["x"] = at.X, ["z"] = at.Z }, cancellationToken).ConfigureAwait(false);
        var spawnTick = (long)surveyed.Tick;
        report["spawned"] = new JsonObject { ["reply"] = spawned.DeepClone(), ["tick"] = spawnTick, ["colonists_before"] = pawns };

        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            using var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false);
            LogRow? replan = null;
            _ = await ServiceJournal.WaitReviewAsync(
                journal,
                new Wait { Ceiling = TimeSpan.FromMinutes(4), Stall = Wait.StallBudget(), Terminal = service.Exited },
                r =>
                {
                    foreach (var row in ServiceLogs.ReadLogs(service.LogPath))
                    {
                        if (IsReplan(row) && Number(row.Fields().GetValueOrDefault("colonists")) == pawns + 1)
                        {
                            replan = row;
                            return true;
                        }
                    }

                    return r.Tick.Value > spawnTick + (2 * ReplanWithin);
                },
                cancellationToken).ConfigureAwait(false);
            if (replan is null)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no layout_plan replanned row for {pawns + 1} colonists by tick {spawnTick + (2 * ReplanWithin)} (spawned at {spawnTick})"));
            }

            report["replan"] = new JsonObject
            {
                ["tick"] = replan.Tick,
                ["payload"] = ToJson(replan.Payload),
                ["after_ticks"] = replan.Tick - spawnTick,
            };
            if (replan.Tick - spawnTick > ReplanWithin)
            {
                throw new AcceptanceException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"layout_plan replanned for {pawns + 1} colonists at tick {replan.Tick}, {replan.Tick - spawnTick} ticks after the colonist joined (bound {ReplanWithin})"));
            }
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves until the journal holds a layout plan and one further hour has passed, so the field planner has zoned
    /// crops, then stops the service.
    /// </summary>
    private static async Task<LayoutPlan> ServePlannedAsync(ISession s, Report report, CancellationToken cancellationToken)
    {
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            using var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false);
            LayoutPlan? plan = null;
            long planTick = 0;
            try
            {
                _ = await ServiceJournal.WaitReviewAsync(
                    journal,
                    new Wait { Ceiling = TimeSpan.FromMinutes(3), Stall = Wait.StallBudget(), Terminal = service.Exited },
                    r =>
                    {
                        var record = journal.LayoutPlan(r.Snapshot, r.Tick, cancellationToken);
                        if (record?.Plan is not { } found)
                        {
                            return false;
                        }

                        plan = found;
                        planTick = record.Tick.Value;
                        return r.Tick.Value >= planTick + ReplanWithin;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("wait for a layout plan and its first field hour: " + e.Message, e);
            }

            report["plan"] = new JsonObject { ["tick"] = planTick, ["summary"] = plan!.Summary() };
            return plan!;
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads every growing zone's cells natively.</summary>
    private static async Task<IReadOnlyList<IReadOnlyList<Cell>>> CropZonesAsync(ISession s, Harness h, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            "zones",
            "observations_list_zones",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var grid = await h.MapCellsAsync("zone-cells", s.Identity, cancellationToken).ConfigureAwait(false);
        var zones = grid.ZoneCells();
        var output = new List<IReadOnlyList<Cell>>();
        foreach (var raw in observed["zones"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (!string.Equals(row["type"].AsString(), "growing", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            output.Add(zones.GetValueOrDefault(row["id"].AsString()) ?? []);
        }

        return output;
    }

    /// <summary>Reports a layout replan row: the layout_plan decision with verdict replanned.</summary>
    private static bool IsReplan(LogRow row) =>
        row.Kind == "layout_plan" && row.Fields().GetValueOrDefault("verdict") is string verdict && verdict == "replanned";

    private static long Number(object? value) => value switch
    {
        double d => (long)d,
        long l => l,
        int i => i,
        _ => -1,
    };

    /// <summary>A decoded journal payload as a report node, built without reflection.</summary>
    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        double d => JsonValue.Create(d),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        IReadOnlyDictionary<string, object?> map => new JsonObject(map.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => KeyValuePair.Create(e.Key, ToJson(e.Value)))),
        IDictionary<string, object?> map => new JsonObject(map.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => KeyValuePair.Create(e.Key, ToJson(e.Value)))),
        IEnumerable list => new JsonArray([.. list.Cast<object?>().Select(ToJson)]),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };
}
