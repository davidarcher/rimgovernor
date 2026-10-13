using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/ladder</c> (Go <c>ladder.go</c>), the multi-stage production case: on the Core tribal baseline, the colony's
/// component need (construction demand or recurring spend) asks for an item only a research-gated bench produces (components
/// on a fabrication bench), and the ladder walks research -> bench -> workstation stockpile -> bill. The fixture stages what
/// the ladder does not build: a roofed starter hut whose native room role hosts the Workshop facility (with a sleeping spot
/// per colonist, so the initial shelter is met), a simple research bench and a fueled generator inside it, steel, wood and the
/// bench's components loose beside its door (outside storage, so the stock reads 0), and Fabrication research a few points
/// short of done.
/// Passing needs live native evidence, never the journal alone: Fabrication finished natively, a fabrication bench standing
/// in a Workshop-hosting room carrying the component bill, an allow-list stockpile for its ingredients inside that room, and
/// the stored component count above the pre-service baseline.
/// </summary>
internal static class ProductionLadder
{
    private const string Prepare = "test/production_ladder_prepare";
    private const string AuditOp = "test/production_ladder_audit";
    private const string Recipe = "Make_ComponentIndustrial";
    private const string Project = "Fabrication";

    // The component floor of PolicyFunctions.DefaultResourceTargets().
    private const int Target = 10;

    private const string BenchStage = "bench-built";

    // The checkpoint state key the prepare phase records the pre-service component count under, for the postmortem of a later run.
    private const string BaselineKey = "baseline_count";

    // How long the ladder gets to finish the research rung and raise its bench (the "bench-built" stage, cached across
    // runs); Window is how long the staged colony then gets to land its first product. Each watch ends early on its rung.
    private static readonly TimeSpan BenchWindow = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(13);

    // Includes the workshop, research and ingredient-stockpile stages plus emergency responders: injury, predators and
    // choice dialogs can block development until handled. Shelter and sleeping stay off because the fixture already
    // provides a hut and sleeping spots; additional shells compete with the workshop for floor space. An unserved
    // priority-2 shelter goal gates comfort, never MaintainResource.
    private static readonly string[] Families =
    [
        "temperature", "comfort", "work", "power", "supply", "defense", "tend", "rescue", "medical", "field", "stockpiles",
        "acquisition", "cooking", "resource", "workshop", "research", "gear", "armory", "dialog",
    ];

    // Keeps the watch's fail-fast on but lets MaintainResource sit method_unavailable through the research rung: while the
    // project the workshop recorded as gating the bench is unfinished the goal holds no method of its own by design
    // (RoundsFindings), so those reviews are not the planner committing nothing under a slot it was handed. MaintainResource
    // also keeps other resources (medicine) stocked, and one of those acquisitions falling short (outcome_not_achieved) is
    // retried, not the ladder failing; so is an acquisition native refused on a stale census.
    private static readonly FailFast LadderFailFast = new()
    {
        MethodUnavailableWaits = true,
        RetryableUnsuccessful = ["outcome_not_achieved", "refused"],
    };

    internal static AcceptanceCase Case() => new()
    {
        Name = "production/ladder",
        Scope = string.Create(CultureInfo.InvariantCulture, $"MaintainResource {PolicyFunctions.ComponentResource.Value}:{Target} walks research ({Project}) -> fabrication bench -> ingredient stockpile -> bill; the live item count must rise above the pre-service baseline (issue #4, M4)."),
        Start = new FixtureStart
        {
            Op = Prepare,
            ArgsFrom = StarterSiteHost.ArgsFor(11),
            Args = new JsonObject(),
            On = new SaveStart { Name = Baselines.BaselineName },
        },
        Serve = new ServeSpec { Families = Families, NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "production" },
        Stages = [BenchStage],
        Budget = BenchWindow + Window + TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Reason = "the research rung and the bench build are a cached stage (#329); the stockpile and the first bill iteration after it run on a miss and a hit alike",
        Run = RunAsync,
        Postmortem = PostmortemAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var observed = ObservedSession.Of(s);
        var concern = PolicyFunctions.MaintainResource.Value;
        await s.StageAsync(
            BenchStage,
            async ct =>
            {
                _ = await SustainedFood.ObserveAsync(
                    observed,
                    new SustainedObservation
                    {
                        Watch = new WatchConfig { Watch = BenchWindow, Concern = concern, Until = BenchBuilt, FailFast = LadderFailFast },
                        Prepare = async (h, report, c) =>
                        {
                            var prepared = s.Prepared ?? [];
                            report["fixture"] = prepared;
                            if (prepared["finished"].AsBool() == true)
                            {
                                throw new AcceptanceException($"{Project} is already finished; the research rung has nothing to prove");
                            }

                            var before = await h.CallAsync("baseline-audit", AuditOp, new JsonObject(), c).ConfigureAwait(false);
                            var baseline = ProductCount(before);
                            report["baseline_count"] = baseline;

                            // The bill path and the postmortem read the baseline back from the bundle (RestoredState) on a hit or a -postmortem-only rerun.
                            Checkpoints.SetState(BaselineKey, JsonValue.Create(baseline));
                            if (baseline >= Target)
                            {
                                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"save already holds {baseline} {PolicyFunctions.ComponentResource.Value}; the stock floor {Target} leaves no deficit to recover"));
                            }

                            if (Benches(before).Count != 0)
                            {
                                throw new AcceptanceException("save already holds a fabrication bench; the bench rung has nothing to prove");
                            }
                        },
                        Audit = async (h, report, c) =>
                        {
                            var live = await h.CallAsync("bench-audit", AuditOp, new JsonObject(), c).ConfigureAwait(false);
                            report["bench_stage"] = live;
                            if (live["finished"].AsBool() != true)
                            {
                                throw new AcceptanceException($"{Project} did not finish natively within the bench window: progress={live["progress"].ToJson()} current={live["current"].ToJson()}");
                            }

                            if (Benches(live).Count == 0)
                            {
                                throw new AcceptanceException($"no fabrication bench stands within the bench window ({live.ToJson()})");
                            }
                        },
                    },
                    ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (StagePlanning.RestoredState(s.Resumed, s.Staged, BaselineKey) is { } restored)
        {
            s.Report["baseline_count"] = restored.AsNumber();
        }

        _ = await SustainedFood.ObserveAsync(
            observed,
            new SustainedObservation { Watch = new WatchConfig { Watch = Window, Concern = concern, Until = BillProduced, FailFast = LadderFailFast } },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task PostmortemAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        double baseline;
        if (report.Has("baseline_count") && report["baseline_count"] is JsonValue)
        {
            baseline = report["baseline_count"].AsNumber();
        }
        else
        {
            var restored = StagePlanning.RestoredState(s.Resumed, s.Staged, BaselineKey)
                ?? throw new AcceptanceException($"no {BaselineKey} in the report or the bundle's state: the prepare phase never ran");
            baseline = restored.AsNumber();
            report["baseline_count"] = baseline;
        }

        IWatchStore journal;
        try
        {
            journal = await ObservedSession.OpenStoreAsync(s.Config, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("reopen journal: " + e.Message, e);
        }

        try
        {
            await AuditAsync(s.Harness, journal, report, baseline, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            (journal as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Reports a sample whose MaintainResource goal holds or held a production bill plan with every action completed: the
    /// first product landed.
    /// </summary>
    private static bool BillProduced(JsonObject sample) =>
        PlanCompleted(sample, (_, kinds, actions) => (int)kinds["production_bill"].AsNumber() == actions);

    /// <summary>
    /// Reports a sample whose MaintainResource goal holds or held (retired_plans: a completed method leaves the goal at the
    /// next review) a workshop bench plan with every action completed: the bench stands.
    /// </summary>
    private static bool BenchBuilt(JsonObject sample) =>
        PlanCompleted(sample, (method, _, _) => method.StartsWith("workshop-", StringComparison.Ordinal) && !method.StartsWith("workshop-shell", StringComparison.Ordinal));

    /// <summary>Reports a plan match accepts (by its method, action kinds and count), active or retired, with every action completed.</summary>
    private static bool PlanCompleted(JsonObject sample, Func<string, JsonObject, int, bool> match)
    {
        foreach (var raw in sample["plans"].AsSlice().Concat(sample["retired_plans"].AsSlice()))
        {
            var plan = raw.AsMap() ?? [];
            var method = plan["method"].AsString();
            var kinds = plan["kinds"].AsMap() ?? [];
            var actions = (int)plan["actions"].AsNumber();
            var completed = (int)(plan["stages"].AsMap()?["completed"].AsNumber() ?? 0);
            if (actions > 0 && match(method, kinds, actions) && completed == actions)
            {
                return true;
            }
        }

        return false;
    }

    private static double ProductCount(JsonObject audit) => audit["components"].AsNumber();

    private static List<JsonObject> Benches(JsonObject audit) => [.. audit["benches"].AsSlice().Select(raw => raw.AsMap() ?? [])];

    /// <summary>The room roles whose rooms host the Workshop facility (Go <c>workshop.Hosts</c>).</summary>
    private static bool WorkshopHosts(string role)
    {
        var workshop = PolicyFunctions.Facility(RoomRole.Workshop);
        return Enum.TryParse<RoomRole>(role, ignoreCase: false, out var parsed) && workshop.Hosts(parsed);
    }

    /// <summary>Compares the journal's goals with the live research state, bench, stockpile and item count after the service has stopped.</summary>
    private static async Task AuditAsync(Harness h, IWatchStore journal, Report report, double baseline, CancellationToken cancellationToken)
    {
        var resource = PolicyFunctions.MaintainResource.Value;
        var goal = await journal.StandardSummaryAsync(resource, cancellationToken).ConfigureAwait(false)
            ?? throw new AcceptanceException($"{resource} was never bound in the rounds");
        report[resource.ToLowerInvariant() + "_standard"] = goal;

        // EnsureResearch is a Project: its own row, not a goal.
        var researchConcern = PolicyFunctions.EnsureResearch.Value;
        var research = await journal.StandardSummaryAsync(researchConcern, cancellationToken).ConfigureAwait(false)
            ?? throw new AcceptanceException($"{researchConcern} was never bound in the rounds");
        report[researchConcern.ToLowerInvariant() + "_standard"] = research;
        report["production_ladder"] = await journal.ProductionLadderAsync(cancellationToken).ConfigureAwait(false);

        var live = await h.CallAsync("final-audit", AuditOp, new JsonObject(), cancellationToken).ConfigureAwait(false);
        report["live"] = live;
        if (live["finished"].AsBool() != true)
        {
            throw new AcceptanceException($"{Project} did not finish natively: progress={live["progress"].ToJson()} current={live["current"].ToJson()}");
        }

        var hosted = new JsonArray();
        foreach (var row in Benches(live))
        {
            if (!WorkshopHosts(row["roomRole"].AsString()))
            {
                continue;
            }

            foreach (var bill in row["bills"].AsSlice())
            {
                if (bill.AsString() == Recipe)
                {
                    hosted.Add(row.DeepClone());
                }
            }
        }

        report["hosted_benches"] = hosted;
        if (hosted.Count == 0)
        {
            throw new AcceptanceException($"no fabrication bench inside a room hosting the Workshop facility carries a {Recipe} bill: {live["benches"].ToJson()}");
        }

        var stockpiles = new JsonArray();
        foreach (var raw in live["stockpiles"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["allowsSteel"].AsBool() == true && WorkshopHosts(row["roomRole"].AsString()) && row["allowedCount"].AsNumber() <= 16)
            {
                stockpiles.Add(row.DeepClone());
            }
        }

        report["ingredient_stockpiles"] = stockpiles;
        if (stockpiles.Count == 0)
        {
            throw new AcceptanceException($"no allow-list stockpile for steel stands inside a room hosting the Workshop facility: {live["stockpiles"].ToJson()}");
        }

        var count = ProductCount(live);
        report["final_count"] = count;
        if (count <= baseline)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{PolicyFunctions.ComponentResource.Value} count did not rise: baseline={baseline} final={count}"));
        }
    }
}
