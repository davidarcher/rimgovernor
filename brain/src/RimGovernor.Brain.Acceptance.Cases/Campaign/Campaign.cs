using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The campaign family's shared shape (Go <c>campaign/campaign.go</c>): the proof of autonomy, an unassisted campaign
/// on a declared fixture, seed and mod set, driven through the player control path, where the harness assists only
/// during setup and every later hand is recorded as an intervention. Two campaigns (campaign/foothold,
/// campaign/recovery) share one shape: launch the service over the tribal8 baseline, watch a tick-measured phase,
/// stop, inject the next disturbance through a fixture op, relaunch. Every assertion is native end state or an
/// advancing ConcernProgress record, never a plan count. The player control path is Ultrafast under the player pacing.
/// </summary>
internal sealed class CampaignRun
{
    // The declared fixture: the committed eight-colonist tribal baseline, Core only, the save's own seed. The report's
    // manifest row names all three so a failure is reproducible.
    internal const string FixtureSave = SustainedFoodCase.BaselineSave;
    internal const string ModSet = "Core";

    /// <summary>The environment variable that overrides the foothold window in game ticks; the shorter recovery and fault windows scale with it.</summary>
    internal const string WindowTicksEnv = "RIMGOVERNOR_ACCEPT_CAMPAIGN_TICKS";

    // The foothold campaign's window: three game days, the stretch on which the tribal8 baseline's shelter and food
    // goals settle under every family (sustained/colony samples five).
    private const ulong FootholdTicks = 3 * 60000;

    private static readonly string[] FixtureOps = ["test/defense_setup", "test/hut_shell_fixture"];

    private readonly ISession _session;
    private readonly Report _report;
    private readonly List<JsonObject> _phases = [];
    private readonly List<JsonObject> _milestones = [];
    private readonly List<JsonObject> _interventions = [];
    private readonly List<JsonObject> _injections = [];
    private bool _setup;
    private int _launches;

    private CampaignRun(ISession session)
    {
        _session = session;
        _report = session.Report;
    }

    internal static ulong FootholdWindow()
    {
        var raw = Environment.GetEnvironmentVariable(WindowTicksEnv);
        if (!string.IsNullOrEmpty(raw) && ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            return n;
        }

        return FootholdTicks;
    }

    /// <summary>
    /// The campaign's serve process: every routine family (null Families; the supply family must be in, or
    /// the admin/supply-safety Need parks every concern after a raid), the player control path's clock flags and the campaign's
    /// own request prefix. The player's Ultrafast is the control path, which serve runs under the player pacing.
    /// </summary>
    internal static ServeSpec ServeSpecFor(string prefix) => new()
    {
        NativeTimeout = TimeSpan.FromSeconds(15),
        StepStall = TimeSpan.FromSeconds(90),
        Prefix = prefix,
        ClockSpeed = "Ultrafast",
    };

    /// <summary>
    /// The shape every case in the family shares: the baseline save with live needs, quiet storyteller (the
    /// disturbances are staged, not drawn) and a serve process on the player control path.
    /// </summary>
    internal static AcceptanceCase CampaignCase(string name, string scope, string reason, TimeSpan budget, Func<ISession, CancellationToken, Task> run) => new()
    {
        Name = name,
        Tier = CaseTier.Nightly,
        Scope = scope,
        Start = new SaveStart { Name = FixtureSave },
        Keep = [NeedDef.LiveNeeds.Value],
        Quiet = QuietMode.Required,
        Serve = ServeSpecFor("campaign"),
        RequiredOps = FixtureOps,
        Reason = reason,
        Budget = budget,
        Crew = new Crew { Size = 3 },
        Run = run,
    };

    /// <summary>
    /// The Concerns every campaign sample reads beside the food/supply Need: the foothold gates and the wood stock the
    /// recovery campaign breaches.
    /// </summary>
    internal static readonly IReadOnlyList<string> CampaignConcerns =
    [
        ShelterFamily.Starter.Value,
        ShelterFamily.Sleeping.Value,
        ShelterFamily.Expansion.Value,
        PolicyFunctions.CookingNeed.Value,
        ShelterFamily.Temperature.Value,
        EconomyFamily.Resource.Value,
        MilitaryFamily.Defense.Value,
    ];

    internal static CampaignRun Create(ISession s)
    {
        var c = new CampaignRun(s);
        c._report["manifest"] = new JsonObject
        {
            ["fixture"] = FixtureSave,
            ["mods"] = new JsonArray(ModSet),
            ["seed"] = s.Identity["seed"].AsString(),
            ["identity"] = s.Identity.DeepClone(),
            ["control"] = "player",
        };
        c.Publish();
        c._report["intervention_count"] = 0;
        foreach (var op in FixtureOps)
        {
            if (!Dyn.Contains(s.Names, op))
            {
                throw new AcceptanceException($"missing {op} in discovery; rebuild the native mod with -Fixture DefenseFixture,HutShellFixture");
            }
        }

        return c;
    }

    private static JsonArray Rows(List<JsonObject> rows) => new([.. rows.Select(row => (JsonNode?)row.DeepClone())]);

    private void Publish()
    {
        _report["interventions"] = Rows(_interventions);
        _report["injections"] = Rows(_injections);
        _report["milestones"] = Rows(_milestones);
        _report["phases"] = Rows(_phases);
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal ISession Session => _session;

    internal Report Report => _report;

    /// <summary>Marks the end of setup: everything the harness does to the colony from here is an intervention.</summary>
    internal void SetupDone() => _setup = true;

    /// <summary>Records a harness hand on the colony under <paramref name="label"/>; after setup it counts against the campaign.</summary>
    internal void Intervene(string label, JsonObject? detail) => Record(label, _setup, detail);

    /// <summary>Files an intervention row, counted when <paramref name="afterSetup"/>.</summary>
    private void Record(string label, bool afterSetup, JsonObject? detail)
    {
        var row = new JsonObject { ["label"] = label, ["after_setup"] = afterSetup, ["at"] = Stamp() };
        foreach (var (key, value) in detail ?? [])
        {
            row[key] = value?.DeepClone();
        }

        _interventions.Add(row);
        _report["interventions"] = Rows(_interventions);
        _report["intervention_count"] = InterventionCount();
    }

    /// <summary>The number of interventions after setup.</summary>
    internal int InterventionCount() => _interventions.Count(row => row["after_setup"].AsBool() == true);

    /// <summary>Records a staged disturbance (the scenario's own subject).</summary>
    internal void Inject(string label, JsonObject? detail)
    {
        var row = new JsonObject { ["label"] = label, ["at"] = Stamp() };
        foreach (var (key, value) in detail ?? [])
        {
            row[key] = value?.DeepClone();
        }

        _injections.Add(row);
        _report["injections"] = Rows(_injections);
    }

    /// <summary>Records a named campaign milestone at a game tick.</summary>
    internal void Milestone(string name, ulong tick)
    {
        _milestones.Add(new JsonObject { ["name"] = name, ["tick"] = tick });
        _report["milestones"] = Rows(_milestones);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"MILESTONE {name} tick={tick}"));
    }

    /// <summary>The campaign's own gate: no intervention after setup.</summary>
    internal AcceptanceException? AssertUnassisted()
    {
        var n = InterventionCount();
        if (n == 0)
        {
            return null;
        }

        var labels = _interventions.Where(row => row["after_setup"].AsBool() == true).Select(row => row["label"].AsString());
        return new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{n} intervention(s) after setup: [{string.Join(' ', labels)}]"));
    }

    /// <summary>
    /// Launches the service, watches the phase, then stops it and reattaches the harness. Every launch after the first
    /// is the scenario's restart around an injection, not assistance; the authority hand at launch is recorded either way.
    /// </summary>
    internal async Task<CampaignPhase> PlayAsync(string label, WatchConfig watch, CancellationToken cancellationToken)
    {
        _launches++;
        var spec = ServeSpecFor("campaign-" + label);
        ServiceProcess service;
        try
        {
            service = await _session.ServeAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: serve: {e.Message}", e);
        }

        var p = new CampaignPhase(label);
        var stopped = false;
        async Task StopAsync()
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            if (await service.StopAsync().ConfigureAwait(false) is { } keep)
            {
                p.Keep = keep;
                _report["authority_reacquisitions"] = keep.DeepClone();
            }
        }

        try
        {
            // The authority grant at launch establishes control (setup, or the scenario's restart around an
            // injection); the keep-alive's re-acquisitions during the phase are hands on the colony.
            Record("acquire-" + label, false, new JsonObject { ["kind"] = "authority", ["launch"] = _launches, ["restart"] = _launches > 1 });
            var cfg = watch with
            {
                Concern = watch.Concern.Length == 0 ? PolicyFunctions.FoodSupplyNeed.Value : watch.Concern,
                Poll = watch.Poll <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : watch.Poll,
            };
            var report = new Report();
            var timeline = new List<JsonObject>();
            try
            {
                await SustainedFood.WatchAsync(_session.Config, ObservedSession.Watched(_session, service), cfg, report, timeline, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                p.Err = e;
            }

            p.Timeline = timeline;
            p.Window = report["window"].AsMap()?.DeepClone() as JsonObject;
            if (service.Exited() is null)
            {
                p.Progress = await ReadProgressAsync(service, cancellationToken).ConfigureAwait(false);
                try
                {
                    var state = await service.ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
                    p.Mode = state.Body.Member("mode").AsString();
                }
                catch (AcceptanceException tolerated)
                {
                    // The mode stays empty; assertPlaying names it.
                    Tolerated.Note("state read", tolerated);
                }
            }

            await StopAsync().ConfigureAwait(false);
            var reacquired = (int)p.Keep.Member("reacquired").AsNumber();
            if (reacquired > 0)
            {
                Intervene("keepalive-" + label, new JsonObject { ["kind"] = "authority_reacquired", ["count"] = reacquired });
            }

            try
            {
                p.Logs = ServiceLogs.ReadLogs(service.LogPath);
            }
            catch (IOException tolerated)
            {
                // A recorder that cannot be read leaves the phase without rows.
                Tolerated.Note("recorder read", tolerated);
            }

            var row = new JsonObject
            {
                ["label"] = label,
                ["launch"] = _launches,
                ["samples"] = p.Timeline.Count,
                ["window"] = p.Window?.DeepClone(),
                ["mode"] = p.Mode,
                ["metrics"] = SustainedFood.DeriveMetrics(p.Timeline).ToJson(),
                ["colony"] = SustainedFood.DeriveColonyOutcome(p.Timeline).ToJson(),
                ["progress"] = new JsonArray([.. p.Progress.Select(r => (JsonNode?)r.ToJson())]),
                ["keepalive"] = p.Keep?.DeepClone(),
                ["clock"] = service.Entry["clock"]?.DeepClone(),
                ["events"] = report["events"]?.DeepClone(),
            };
            if (p.Err is not null)
            {
                row["error"] = p.Err.Message;
            }

            _phases.Add(row);
            _report["phases"] = Rows(_phases);
            _report["timeline"] = new JsonArray([.. p.Timeline.Select(sample => (JsonNode?)sample.DeepClone())]);
            try
            {
                _ = _session.Reattach();
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{label}: reattach: {e.Message}", e);
            }

            return p;
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads the live service's concern progress records.</summary>
    private static async Task<List<ProgressRecord>> ReadProgressAsync(ServiceProcess service, CancellationToken cancellationToken)
    {
        var records = new List<ProgressRecord>();
        try
        {
            var reply = await service.ApiAsync("GET", "/api/routines", null, "", cancellationToken).ConfigureAwait(false);
            if (reply.Status != 200)
            {
                return records;
            }

            foreach (var raw in reply.Body.Member("progress").AsSlice())
            {
                var row = raw.AsMap() ?? [];
                records.Add(new ProgressRecord(
                    row["concern"].AsString(),
                    row["method"].AsString(),
                    row["expected"].AsString(),
                    row["lastProgress"].AsUInt64() ?? 0,
                    row["nextReview"].AsUInt64() ?? 0,
                    row["blocked"].AsString()));
            }
        }
        catch (AcceptanceException tolerated)
        {
            // Go ignored the read error: the phase keeps no progress records, and assertProgress names that.
            Tolerated.Note("progress read", tolerated);
        }

        return records;
    }

    /// <summary>
    /// Calls a test op on the paused game through the reattached harness, refusing a reply without success.
    /// </summary>
    internal async Task<JsonObject> FixtureAsync(string label, string op, JsonObject args, CancellationToken cancellationToken)
    {
        var h = _session.Harness;
        _ = await h.CallAsync(label + "-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var output = await h.CallAsync(label, op, args, cancellationToken).ConfigureAwait(false);
        if (output["success"].AsBool() == false)
        {
            throw new AcceptanceException($"{op} {args.ToJson()} refused: {output.ToJson()}");
        }

        return output;
    }

    /// <summary>
    /// The native end state every campaign phase must leave: no colonist lost, none past Malnutrition 0.3, indoor
    /// sleeping capacity for every colonist (shelter) and a known food runway. It records the facts it read on report[label].
    /// </summary>
    internal async Task AuditColonyAsync(Harness h, string label, IReadOnlyList<string> initial, CancellationToken cancellationToken)
    {
        var failures = new List<Exception?>
        {
            await SustainedFoodCase.CaptureAsync(() => SustainedColonyCase.AuditNutrition(h, _report, cancellationToken)).ConfigureAwait(false),
        };
        IReadOnlyList<JsonObject> listed;
        try
        {
            listed = await Colonists.ListColonistsAsync(h, label + "-pawns", true, false, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            failures.Add(e);
            throw SustainedFoodCase.Join(failures)!;
        }

        var alive = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in listed)
        {
            alive[Colonists.RowID(row)] = row["dead"].AsBool() != true;
        }

        foreach (var id in initial)
        {
            if (!alive.GetValueOrDefault(id))
            {
                failures.Add(new AcceptanceException($"initial colonist {id} dead or gone"));
            }
        }

        CampaignColonyFacts.Reading facts;
        try
        {
            facts = await CampaignColonyFacts.ReadAsync(h, _session, label + "-facts", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            failures.Add(e);
            throw SustainedFoodCase.Join(failures)!;
        }

        _report[label] = new JsonObject
        {
            ["colonists"] = facts.Colonists,
            ["indoor_capacity"] = facts.IndoorCapacity,
            ["food_days"] = facts.FoodDays,
            ["food_days_known"] = facts.FoodDaysKnown,
            ["wood"] = facts.Wood,
            ["wood_known"] = facts.WoodKnown,
            ["alive"] = alive.Count,
        };
        if (!facts.ColonistsKnown || !facts.IndoorCapacityKnown || facts.IndoorCapacity < facts.Colonists)
        {
            failures.Add(new AcceptanceException(
                $"shelter: indoor sleeping capacity {facts.IndoorCapacity} (known {Bool(facts.IndoorCapacityKnown)}) for {facts.Colonists} colonists (known {Bool(facts.ColonistsKnown)})"));
        }

        if (!facts.FoodDaysKnown || facts.FoodDays <= 0)
        {
            failures.Add(new AcceptanceException(
                string.Create(CultureInfo.InvariantCulture, $"food: runway {facts.FoodDays} days (known {Bool(facts.FoodDaysKnown)})")));
        }

        if (SustainedFoodCase.Join(failures) is { } error)
        {
            throw error;
        }
    }

    /// <summary>Lists the colonists at setup, the roster every audit requires intact.</summary>
    internal static async Task<IReadOnlyList<string>> InitialColonistsAsync(Harness h, Report report, CancellationToken cancellationToken)
    {
        var listed = await Colonists.ListColonistsAsync(h, "initial-colonists", false, false, cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        foreach (var row in listed)
        {
            var id = Colonists.RowID(row);
            if (id.Length > 0)
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            throw new AcceptanceException("no initial colonists observed");
        }

        report["initial_colonists"] = new JsonArray([.. ids.Select(id => (JsonNode?)JsonValue.Create(id))]);
        return ids;
    }

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>One concern's ConcernProgress record as /api/routines renders it.</summary>
internal sealed record ProgressRecord(string Concern, string Method, string Expected, ulong LastProgress, ulong NextReview, string Blocked)
{
    internal JsonObject ToJson() => new()
    {
        ["concern"] = Concern,
        ["method"] = Method,
        ["expected"] = Expected,
        ["lastProgress"] = LastProgress,
        ["nextReview"] = NextReview,
        ["blocked"] = Blocked,
    };
}

/// <summary>One watched stretch of play under a launched service.</summary>
internal sealed class CampaignPhase(string label)
{
    internal string Label { get; } = label;

    internal List<JsonObject> Timeline { get; set; } = [];

    /// <summary>The tick window the watch recorded (the report's <c>window</c> block), null when it recorded none.</summary>
    internal JsonObject? Window { get; set; }

    /// <summary>Every concern's progress record when the phase ended.</summary>
    internal List<ProgressRecord> Progress { get; set; } = [];

    /// <summary>/api/state's mode when the phase ended.</summary>
    internal string Mode { get; set; } = "";

    /// <summary>The service's OTLP log ring.</summary>
    internal IReadOnlyList<LogRow> Logs { get; set; } = [];

    /// <summary>The keep-alive's counters (report authority_reacquisitions).</summary>
    internal JsonObject? Keep { get; set; }

    /// <summary>The watch's own error (a fail-fast verdict, a stall).</summary>
    internal Exception? Err { get; set; }

    private bool Sampled => Window?["tick_sampled"].AsBool() == true;

    private ulong FirstTick => Window?["first_tick"].AsUInt64() ?? 0;

    private ulong LastWindowTick => Window?["last_tick"].AsUInt64() ?? 0;

    /// <summary>The last sampled game tick.</summary>
    internal ulong LastTick()
    {
        for (var i = Timeline.Count - 1; i >= 0; i--)
        {
            if (Timeline[i]["tick"].AsUInt64() is { } tick)
            {
                return tick;
            }
        }

        return 0;
    }

    /// <summary>How far the game tick moved during the phase.</summary>
    internal ulong Advanced() => Sampled ? LastWindowTick - FirstTick : 0;

    /// <summary>
    /// The sustained-progress gate: at least one concern record advanced on native evidence during the phase
    /// (LastProgress at or past its first tick), and no unblocked record sat past its NextReview deadline without
    /// progress or a named blocker (the contract rotates or cools such a method down, so a record left there is a
    /// stall the policy missed).
    /// </summary>
    internal AcceptanceException? AssertProgress()
    {
        if (!Sampled)
        {
            return new AcceptanceException($"{Label}: no sampled tick, progress cannot be judged");
        }

        var advanced = 0;
        var stalled = new List<string>();
        foreach (var r in Progress)
        {
            if (r.LastProgress >= FirstTick)
            {
                advanced++;
            }
            else if (r.Blocked.Length == 0 && r.NextReview > 0 && r.NextReview < LastWindowTick)
            {
                stalled.Add($"{r.Concern}/{r.Method} last={r.LastProgress} next={r.NextReview}");
            }
        }

        var failures = new List<Exception?>();
        if (advanced == 0)
        {
            failures.Add(new AcceptanceException($"{Label}: no concern progress record advanced between ticks {FirstTick} and {LastWindowTick} ({Progress.Count} records)"));
        }

        if (stalled.Count > 0)
        {
            failures.Add(new AcceptanceException($"{Label}: {stalled.Count} concern(s) past their progress deadline with no blocker: [{string.Join(' ', stalled)}]"));
        }

        // The first failing gate names the phase; Go returned at the first one.
        return failures.FirstOrDefault() as AcceptanceException;
    }

    /// <summary>
    /// The "must keep playing" gate: the game tick advanced through the phase's window, the service stayed in automate
    /// and the watch raised no stall or fail-fast verdict.
    /// </summary>
    internal AcceptanceException? AssertPlaying(ulong window)
    {
        if (Err is not null)
        {
            return new AcceptanceException($"{Label}: {Err.Message}", Err);
        }

        if (Window?["reached"].AsBool() != true)
        {
            return new AcceptanceException($"{Label}: window of {window} ticks not reached (advanced {Advanced()})");
        }

        if (Mode != "automate")
        {
            return new AcceptanceException($"{Label}: service ended the phase in mode \"{Mode}\", want automate");
        }

        return null;
    }
}

/// <summary>
/// The colony facts the end-state audits judge (Go <c>campaign.ColonyFacts</c>): the food forecast, wood stock and
/// sleeping capacity, read natively through the reattached harness.
/// </summary>
internal static class CampaignColonyFacts
{
    // The service's own request parameters (Go bridge.HerdRadius / HuntRouteBudgetMS): a bare scope leaves the native's
    // wild hunt rows without herd sizes.
    private const uint HerdRadius = 25;
    private const uint HuntRouteBudgetMs = 50;

    /// <summary>The facts a campaign audit reads, each with whether the snapshot carried it.</summary>
    internal readonly record struct Reading(
        ulong Colonists,
        bool ColonistsKnown,
        ulong IndoorCapacity,
        bool IndoorCapacityKnown,
        double FoodDays,
        bool FoodDaysKnown,
        long Wood,
        bool WoodKnown);

    /// <summary>
    /// Reads the controller's colony facts natively through the reattached harness. The food days are the snapshot's
    /// foodRunwayDays; the Go read decoded the full projection and used its forecast, so the two can differ.
    /// </summary>
    internal static async Task<Reading> ReadAsync(Harness h, ISession s, string label, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() },
            ["planning"] = false,
            ["herdRadius"] = HerdRadius,
            ["huntRouteBudgetMs"] = HuntRouteBudgetMs,
            ["huntPredatorMarginCells"] = PolicyFunctions.HuntPredatorMarginCells,
        };
        var reply = await h.WireAsync(label, "observations_read_colony_facts", request, cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        if (observed["context"].Member("identity") is null)
        {
            throw new AcceptanceException($"{label}: colony facts unavailable");
        }

        var wood = 0L;
        var woodKnown = observed["resources"] is JsonArray;
        foreach (var raw in observed["resources"].AsSlice())
        {
            if (raw.Member("defName").AsString() == "WoodLog")
            {
                wood += (long)raw.Member("units").AsNumber();
            }
        }

        var colonists = observed["colonistCount"].AsUInt64();
        var indoor = observed["indoorSleepingCapacity"].AsUInt64();
        var days = observed["foodRunwayDays"];
        return new Reading(
            colonists ?? 0,
            colonists is not null,
            indoor ?? 0,
            indoor is not null,
            days is null ? 0 : days.AsNumber(),
            days is not null,
            wood,
            woodKnown);
    }
}
