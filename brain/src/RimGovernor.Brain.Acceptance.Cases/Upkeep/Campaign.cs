using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using JournalStore = RimGovernor.Brain.Store.Store;
using StoreRounds = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/campaign</c> (Go <c>campaign.go</c>): sustained survival on one colony. The deficits the single-deficit
/// cases stage one at a time are chained on one kept world and one durable journal, and every deficit recovered earlier
/// must stay closed while the later ones are handled. A stage stops the service, takes the slot back, stages its deficit
/// over the live map (never a reload: the routine goals of the world survive a paired restart, so the journal still
/// carries the earlier recoveries), serves again with every family composed so far, follows the stage's own journal
/// watch and audits the native outcome. A recovered goal that is measured in deficit again starts a new Episode
/// (domain.ReviewStandard). Colony traffic can reopen a goal honestly (a medicine stock drawn down again), so the
/// property is the issue's: no deficit reopens without recovery. A reopen is recorded and the goal must be recovered
/// again within reopenTicks and before the stage ends; a goal that is rebound, cancelled or invalidated fails at once.
///
///   medicine -- test/medicine_setup: no medicine, the medical/reserves Need.
///   cold     -- routine_sleeping_prepare + routine_temperature_prepare (coldSnap: days have passed, so ordinary cold
///               snaps bring the afternoon under the campfire threshold), the shelter/temperature Need; last because a
///               campfire's fuel is finite and its reopening would be the fixture's, not a regression.
///
/// Calendar time is not the property: a season is 900k ticks, over an hour of wall time at Ultrafast, so a season soak
/// stays a detached diagnostic outside the registered tiers.
/// </summary>
internal static class UpkeepCampaign
{
    private static readonly TimeSpan s_campaignBudget = TimeSpan.FromMinutes(25);

    private const string CampaignStateKey = "upkeep_campaign";

    /// <summary>
    /// Bounds how long an earlier stage's goal may sit in deficit again before the campaign fails: a deficit that colony
    /// traffic reopens is a new one the service must close, not a regression, while a goal that never recovers is.
    /// </summary>
    private const long ReopenTicks = 90000;

    /// <summary>Bounds the wall time a stage waits after its own watch for every earlier goal to be recovered again.</summary>
    private static readonly TimeSpan s_recoveredWait = TimeSpan.FromMinutes(4);

    /// <summary>A goal an earlier stage recovered, pinned by identity and Episode.</summary>
    internal sealed class ClosedStandard
    {
        internal required string Stage { get; init; }

        internal required ConcernId Need { get; init; }

        internal StandardId Standard { get; set; }

        internal ulong Episode { get; set; }

        internal JsonObject ToJson() => new()
        {
            ["stage"] = Stage,
            ["need"] = Need.Value,
            ["concern"] = Standard.Value,
            ["epoch"] = Episode,
        };

        internal static ClosedStandard FromJson(JsonObject o) => new()
        {
            Stage = o["stage"].AsString(),
            Need = new ConcernId(o["need"].AsString()),
            Standard = new StandardId(o["concern"].AsString()),
            Episode = o["epoch"].AsUInt64() ?? 0,
        };
    }

    /// <summary>The paired stage bundle's identities guarded by later stages and their timeline, including any Episodes advanced by the closed tracker.</summary>
    private sealed class CampaignState
    {
        internal List<ClosedStandard> Closed { get; } = [];

        internal JsonArray Timeline { get; set; } = [];

        internal JsonObject ToJson() => new()
        {
            ["closed"] = new JsonArray([.. Closed.Select(c => (JsonNode?)c.ToJson())]),
            ["timeline"] = Timeline.DeepClone(),
        };
    }

    private sealed record CampaignStage(
        string Name,
        string Fixture,
        IReadOnlyList<string> Families,
        IReadOnlyList<ConcernId> Needs,
        Func<Harness, JsonObject, Report, CancellationToken, Task<JsonObject>> Prepare,
        Func<ControllerJournal, JsonObject, Report, CancellationToken, Task> Watch,
        Func<Harness, JsonObject, JsonObject, UpkeepCensus, Report, CancellationToken, Task> Verify);

    private static List<CampaignStage> CampaignStages()
    {
        var all = UpkeepSupport.Scenarios();
        static CampaignStage FromScenario(UpkeepScenario sc, params ConcernId[] needs) =>
            new(sc.Name, sc.Fixture, sc.Families, needs, sc.Prepare, sc.Watch, sc.Verify);
        var stages = new List<CampaignStage>
        {
            FromScenario(all["medicine"], PolicyFunctions.MedicalReservesNeed.FilingKey),
            FromScenario(all["cold"], ShelterFamily.Temperature.FilingKey),
        };
        // Days of play have passed by the last stage; the fixture cools the map with ordinary cold snaps rather than
        // failing on a warm afternoon.
        stages[1] = stages[1] with { Prepare = UpkeepSupport.PrepareColdWith(true) };
        return stages;
    }

    internal static AcceptanceCase Case()
    {
        var stages = CampaignStages();
        var names = stages.Select(st => st.Name).ToList();
        return new AcceptanceCase
        {
            Name = "upkeep/campaign",
            Scope = "Sustained colony upkeep (issue #99): on one kept " + Baselines.BaselineName + " colony and one durable journal the deficits "
                + "[" + string.Join(' ', names) + "] are staged in turn over the live map, each recovered by the routine families composed so far and audited natively, "
                + "and no standard an earlier stage recovered reopens (new Episode), is rebound or is invalidated while the later ones are handled.",
            Start = new SaveStart { Name = Baselines.BaselineName },
            Serve = new ServeSpec { Families = CumulativeFamilies(stages, stages.Count), Prefix = UpkeepSupport.Prefix },
            Stages = names,
            Budget = s_campaignBudget,
            Crew = new Crew { Size = 3 },
            Reason = "Two deficits chained on one colony with a service restart between them; each stage alone runs in one to four minutes on the registry runner and the chain is the property under test.",
            Run = RunAsync,
        };
    }

    /// <summary>The families every stage reviews: those of every goal up to and including stage <paramref name="n"/>.</summary>
    private static List<string> CumulativeFamilies(List<CampaignStage> stages, int n)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var st in stages.Take(n))
        {
            foreach (var family in st.Families)
            {
                if (seen.Add(family))
                {
                    output.Add(family);
                }
            }
        }

        return output;
    }

    private static CampaignState RestoreCampaignState(ISession s)
    {
        var state = new CampaignState();
        if (StagePlanning.RestoredState(s.Resumed, s.Staged, CampaignStateKey).AsMap() is { } restored)
        {
            foreach (var row in restored["closed"].AsSlice())
            {
                if (row.AsMap() is { } map)
                {
                    state.Closed.Add(ClosedStandard.FromJson(map));
                }
            }

            state.Timeline = restored["timeline"] is JsonArray timeline ? (JsonArray)timeline.DeepClone() : [];
        }

        return state;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var identity = s.Identity;
        var stages = CampaignStages();
        foreach (var st in stages)
        {
            if (!Dyn.Contains(s.Names, st.Fixture))
            {
                throw new AcceptanceException($"missing {st.Fixture} in discovery; rebuild the native mod with -Fixture UpkeepFixture,ForecastFixture,RoundsSleepingFixture");
            }
        }

        var state = RestoreCampaignState(s);
        report["timeline"] = state.Timeline;
        var rebind = StagePlanning.RestoredState(s.Resumed, s.Staged, CampaignStateKey) is not null;
        for (var i = 0; i < stages.Count; i++)
        {
            var st = stages[i];
            var index = i;
            await s.StageAsync(st.Name, async ct =>
            {
                var h = s.Harness;
                var started = DateTimeOffset.UtcNow;
                var stageReport = new Report();
                report["stage_" + st.Name] = stageReport.Data;
                var families = CumulativeFamilies(stages, index + 1);
                stageReport["families"] = new JsonArray([.. families.Select(f => (JsonNode?)f)]);
                try
                {
                    _ = await h.CallAsync(st.Name + "-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, ct).ConfigureAwait(false);
                    var prepared = await st.Prepare(h, identity, stageReport, ct).ConfigureAwait(false);
                    stageReport["prepared"] = prepared;
                    var before = await UpkeepSupport.ReadUpkeepAsync(h, identity, st.Name + "-upkeep-before", ct).ConfigureAwait(false);
                    stageReport["upkeep_before"] = before.ToJson();

                    var service = await s.ServeAsync(s.Spec with { Families = families }, ct).ConfigureAwait(false);
                    List<ClosedStandard>? recovered = null;
                    Exception? stageError = null;
                    try
                    {
                        recovered = await RunStageAsync(s, service, st, prepared, stageReport, state.Closed, rebind, ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is AcceptanceException or OperationCanceledException or RimGovernor.Brain.Domain.DomainException)
                    {
                        stageError = e;
                    }

                    rebind = false;
                    var keep = await service.StopAsync().ConfigureAwait(false);
                    stageReport["authority_reacquisitions"] = keep?.DeepClone();
                    // The postmortem read still runs when the budget cancelled the token.
                    using var afterCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    var afterCt = ct.IsCancellationRequested ? afterCts.Token : ct;
                    try
                    {
                        h = await ReattachPausedAsync(s, afterCt).ConfigureAwait(false);
                    }
                    catch (Exception e) when (stageError is not null && e is AcceptanceException or OperationCanceledException)
                    {
                        throw new AcceptanceException($"stage {st.Name}: {stageError.Message}; {e.Message}", e);
                    }

                    if (stageError is not null)
                    {
                        try
                        {
                            stageReport["upkeep_postmortem"] = (await UpkeepSupport.ReadUpkeepAsync(h, identity, st.Name + "-upkeep-postmortem", afterCt).ConfigureAwait(false)).ToJson();
                        }
                        catch (AcceptanceException readError)
                        {
                            stageReport["upkeep_postmortem_error"] = readError.Message;
                        }

                        throw new AcceptanceException($"stage {st.Name}: {stageError.Message}", stageError);
                    }

                    var after = await UpkeepSupport.ReadUpkeepAsync(h, identity, st.Name + "-upkeep-after", ct).ConfigureAwait(false);
                    stageReport["upkeep_after"] = after.ToJson();
                    await st.Verify(h, identity, prepared, after, stageReport, ct).ConfigureAwait(false);
                    state.Closed.AddRange(recovered!);
                    var row = new JsonObject
                    {
                        ["stage"] = st.Name,
                        ["tick_before"] = before.Tick,
                        ["tick_after"] = after.Tick,
                        ["wall_ms"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                    };
                    foreach (var c in recovered!)
                    {
                        row[c.Need.Value] = new JsonObject { ["concern"] = c.Standard.Value, ["epoch"] = c.Episode };
                    }

                    state.Timeline.Add(row);
                    report["timeline"] = state.Timeline;
                    // Freeze the state: the next stage's tracker mutates its epochs while the periodic ring can be
                    // serializing this checkpoint.
                    Checkpoints.SetState(CampaignStateKey, state.ToJson());
                }
                catch (AcceptanceException e) when (!e.Message.StartsWith("stage ", StringComparison.Ordinal))
                {
                    throw new AcceptanceException($"stage {st.Name}: {e.Message}", e);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        report["closed_standards"] = state.Closed.Count;
    }

    /// <summary>
    /// Takes the slot back after a service stop and pauses the clock before the stage's native reads and the next
    /// stage's fixture (which requires a paused map).
    /// </summary>
    internal static Task<Harness> ReattachPausedAsync(ISession s, CancellationToken cancellationToken) => SessionReattach.PausedAsync(s, cancellationToken);

    /// <summary>
    /// Drives one stage's service from authority to the stage's recovery while guarding every earlier stage's goal, and
    /// returns the goals this stage recovered.
    /// </summary>
    private static async Task<List<ClosedStandard>> RunStageAsync(ISession s, ServiceProcess service, CampaignStage st, JsonObject prepared, Report report, List<ClosedStandard> closed, bool rebind, CancellationToken cancellationToken)
    {
        var rootPlanId = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        report["root_plan"] = rootPlanId;
        service.KeepAuthority(cancellationToken);
        using var journal = ControllerJournal.Open(s, service, cancellationToken);
        var (routines, diagnostics) = await service.WaitRoundsAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        report["diagnostic_post_acquire"] = new JsonArray([.. diagnostics.Select(d => (JsonNode?)d.DeepClone())]);
        var review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
        if (rebind)
        {
            // Loading a paired bundle starts a new world generation. Wait for its review rather than accepting the old
            // journal's last review.
            var load = new LoadId(s.Identity["loadToken"].AsString());
            review = await journal.WaitReviewAsync(new Wait { Ceiling = TimeSpan.FromSeconds(90), Stall = Wait.StallBudget() }, r => r.Snapshot.Load == load, cancellationToken).ConfigureAwait(false);
            report["restored_standards"] = ClosedJson(closed);
            await RebindClosedStandardsAsync(closed, review, journal.Store, cancellationToken).ConfigureAwait(false);
            report["loaded_standards"] = ClosedJson(closed);
        }

        report["rounds_review_first"] = routines.DeepClone();
        // The earlier goals are checked on every review while this stage's watch runs, and once more after it: a
        // reopened goal has a tick budget to recover in, and the stage ends only once every one is recovered.
        var tracker = new ClosedTracker(closed);
        var guard = new ClosedGuard(journal, tracker, cancellationToken);
        Exception? watchError = null;
        try
        {
            await st.Watch(journal, prepared, report, guard.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or OperationCanceledException)
        {
            watchError = e;
        }

        var guardError = await guard.StopAsync().ConfigureAwait(false);
        report["reopened"] = tracker.ReopensJson();
        if (guardError is not null)
        {
            throw guardError;
        }

        if (watchError is not null)
        {
            try
            {
                var final = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
                report["rounds_review_at_failure"] = new JsonObject { ["revision"] = final.Revision, ["tick"] = final.Tick.Value };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Evidence only.
                Tolerated.Note("rounds review at failure", e);
            }

            throw watchError is AcceptanceException ? watchError : new AcceptanceException(watchError.Message, watchError);
        }

        try
        {
            await tracker.WaitRecoveredAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            report["reopened"] = tracker.ReopensJson();
        }

        await Rounds.AssertRoundsRunningAsync(service.GetAsync, cancellationToken).ConfigureAwait(false);
        var recovered = new List<ClosedStandard>();
        foreach (var need in st.Needs)
        {
            var goal = await UpkeepSupport.WaitNeedAsync(journal, need, Finding.Met, cancellationToken).ConfigureAwait(false);
            recovered.Add(new ClosedStandard { Stage = st.Name, Need = need, Standard = goal.Standard.Id, Episode = goal.Standard.Episode });
        }

        return recovered;
    }

    private static JsonArray ClosedJson(List<ClosedStandard> closed) => new([.. closed.Select(c => (JsonNode?)c.ToJson())]);

    /// <summary>
    /// Only a bundle load may rebind the guards, once at the first live review. Subsequent reviews and every stage of a
    /// fresh run retain the strict IDs.
    /// </summary>
    private static async Task RebindClosedStandardsAsync(List<ClosedStandard> closed, StoreRounds review, JournalStore store, CancellationToken cancellationToken)
    {
        var bound = new Dictionary<ConcernId, StandardId>();
        foreach (var binding in review.Standards)
        {
            bound[binding.Concern] = binding.Standard;
        }

        foreach (var c in closed)
        {
            if (!bound.TryGetValue(c.Need, out var id))
            {
                throw new AcceptanceException($"restored {c.Need.Value} has no binding in the loaded world's review");
            }

            var goal = await store.LoadStandardAsync(id, cancellationToken).ConfigureAwait(false);
            if (goal.Standard.Status == StandardStatus.Voided)
            {
                throw new AcceptanceException($"restored {c.Need.Value} is voided in the loaded world");
            }

            c.Standard = id;
            c.Episode = goal.Standard.Episode;
        }
    }

    /// <summary>
    /// Follows the goals earlier stages recovered. A goal that enters a new Episode is recorded as a reopen and its
    /// recorded epoch moves on; the failure is a goal that stays in deficit past <see cref="ReopenTicks"/>, is rebound to
    /// another goal, leaves the journal or is cancelled or invalidated.
    /// </summary>
    private sealed class ClosedTracker(List<ClosedStandard> closed)
    {
        private readonly Dictionary<StandardId, long> _deficitSince = [];
        private readonly List<JsonObject> _reopens = [];
        private readonly Lock _gate = new();

        internal JsonArray ReopensJson()
        {
            lock (_gate)
            {
                return new JsonArray([.. _reopens.Select(r => (JsonNode?)r.DeepClone())]);
            }
        }

        /// <summary>Reads the journal once and reports which closed goals are in deficit, or fails on a violation.</summary>
        internal async Task<List<ClosedStandard>> CheckAsync(ControllerJournal journal, CancellationToken cancellationToken)
        {
            var inDeficit = new List<ClosedStandard>();
            if (closed.Count == 0)
            {
                return inDeficit;
            }

            StoreRounds review;
            try
            {
                review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return inDeficit;
            }

            var bound = new Dictionary<ConcernId, StandardId>();
            foreach (var binding in review.Standards)
            {
                bound[binding.Concern] = binding.Standard;
            }

            foreach (var c in closed)
            {
                if (bound.TryGetValue(c.Need, out var id) && id != c.Standard)
                {
                    throw new AcceptanceException($"{c.Need.Value} (recovered in stage {c.Stage} as {c.Standard.Value}) is bound to a new standard {id.Value} in review {review.Revision}");
                }

                RimGovernor.Brain.Store.StandardState goal;
                try
                {
                    goal = await journal.Store.LoadStandardAsync(c.Standard, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return [];
                }
                catch (RimGovernor.Brain.Store.NotFoundException)
                {
                    throw new AcceptanceException($"{c.Need.Value} (recovered in stage {c.Stage} as {c.Standard.Value}) is gone from the journal");
                }

                if (goal.Standard.Status == StandardStatus.Voided)
                {
                    throw new AcceptanceException($"{c.Need.Value} (recovered in stage {c.Stage}) is voided at review {review.Revision}");
                }

                if (goal.Standard.Episode != c.Episode)
                {
                    lock (_gate)
                    {
                        _reopens.Add(new JsonObject
                        {
                            ["need"] = c.Need.Value,
                            ["stage"] = c.Stage,
                            ["concern"] = c.Standard.Value,
                            ["epoch_from"] = c.Episode,
                            ["epoch_to"] = goal.Standard.Episode,
                            ["tick"] = review.Tick.Value,
                            ["review"] = review.Revision,
                        });
                    }

                    c.Episode = goal.Standard.Episode;
                }

                if (goal.Standard.Finding != Finding.Unmet)
                {
                    _deficitSince.Remove(c.Standard);
                    continue;
                }

                if (!_deficitSince.TryGetValue(c.Standard, out var since))
                {
                    since = review.Tick.Value;
                    _deficitSince[c.Standard] = since;
                }

                if (review.Tick.Value - since > ReopenTicks)
                {
                    throw new AcceptanceException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{c.Need.Value} (recovered in stage {c.Stage}) reopened at tick {since} and is still in deficit at review {review.Revision} tick {review.Tick.Value}, past {ReopenTicks} ticks"));
                }

                inDeficit.Add(c);
            }

            return inDeficit;
        }

        /// <summary>Polls until no closed goal is in deficit, or fails on the tracker's violations or the wall bound.</summary>
        internal async Task WaitRecoveredAsync(ControllerJournal journal, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + s_recoveredWait;
            while (true)
            {
                var inDeficit = await CheckAsync(journal, cancellationToken).ConfigureAwait(false);
                if (inDeficit.Count == 0)
                {
                    return;
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    var c = inDeficit[0];
                    throw new AcceptanceException($"{c.Need.Value} (recovered in stage {c.Stage}) reopened at tick {_deficitSince.GetValueOrDefault(c.Standard)} and is still in deficit after {s_recoveredWait}");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Polls the tracker while a stage's watch runs and cancels the watch the moment it sees a violation.</summary>
    private sealed class ClosedGuard
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;
        private Exception? _error;

        internal ClosedGuard(ControllerJournal journal, ClosedTracker tracker, CancellationToken parent)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                        _ = await tracker.CheckAsync(journal, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException tolerated)
                {
                    // Stopped, or the run ended.
                    Tolerated.Note("guard stopped", tolerated);
                }
                catch (AcceptanceException e)
                {
                    Volatile.Write(ref _error, e);
                    await _cts.CancelAsync().ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }

        /// <summary>The watch's token: cancelled when the guard sees a violation.</summary>
        internal CancellationToken Token => _cts.Token;

        /// <summary>Ends the guard and returns the violation it saw, if any.</summary>
        internal async Task<Exception?> StopAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
            _cts.Dispose();
            return Volatile.Read(ref _error);
        }
    }
}
