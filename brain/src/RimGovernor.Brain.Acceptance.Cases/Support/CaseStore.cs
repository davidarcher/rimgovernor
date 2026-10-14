using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The case-side reader of the in-mod brain's state database (the file the harness points the game at with
/// <c>RIMGOVERNOR_STATE</c>, <see cref="ServiceJournal.PathOf"/>): one store handle serving the watch's projections
/// (<see cref="IWatchStore"/>), the quest and clearance cases' journal reads (<see cref="ICaseJournal"/>) and a staging
/// phase's review reads (<see cref="IPhaseStore"/>). Every read is a snapshot of committed rows; nothing here writes.
/// </summary>
internal sealed class CaseStore : ICaseJournal, IPhaseStore
{
    private readonly Journal _store;

    private CaseStore(Journal store) => _store = store;

    /// <summary>Opens the run's journal, retrying while the game is still creating it.</summary>
    internal static async Task<CaseStore> OpenAsync(Config config, CancellationToken cancellationToken) =>
        new(await ServiceJournal.OpenWithRetryAsync(config, cancellationToken).ConfigureAwait(false));

    public void Dispose() => _store.Dispose();

    public async Task<ulong> RoundsRevisionAsync(CancellationToken cancellationToken) =>
        (await ReadAsync("read rounds", ct => _store.LoadRounds(ct), cancellationToken).ConfigureAwait(false)).Revision;

    public Task<JsonObject> SampleStandardAsync(string concern, CancellationToken cancellationToken) =>
        ReadAsync("sample " + concern, ct => SampleStandard(new ConcernId(concern), ct), cancellationToken);

    public Task<IReadOnlyList<JsonObject>> PlanHistoryAsync(int limit, string methodGlob, CancellationToken cancellationToken) =>
        ReadAsync(
            "plan history " + methodGlob,
            ct =>
            {
                var all = _store.PlanHistoryWithMethods(256, ["*"], ct);
                var rank = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < all.Count; i++)
                {
                    rank[all[i].Spec.Id.Value] = i;
                }

                var rows = new List<JsonObject>();
                foreach (var plan in _store.PlanHistoryWithMethods(Math.Clamp(limit, 1, 256), [methodGlob], ct))
                {
                    rows.Add(new JsonObject
                    {
                        ["plan"] = plan.Spec.Id.Value,
                        ["method"] = plan.Method.Value,
                        ["order"] = rank.TryGetValue(plan.Spec.Id.Value, out var order) ? order : all.Count,
                        ["progress"] = new JsonArray([.. plan.Progress.Select(p => (JsonNode?)ProgressRow(p))]),
                    });
                }

                return (IReadOnlyList<JsonObject>)rows;
            },
            cancellationToken);

    public Task<JsonObject?> StandardSummaryAsync(string concern, CancellationToken cancellationToken) =>
        ReadAsync(
            "standard summary " + concern,
            ct =>
            {
                var review = _store.LoadRounds(ct);
                if (BoundStandard(review, new ConcernId(concern)) is not { } id)
                {
                    return null;
                }

                var goal = _store.LoadStandard(id, ct);
                return new JsonObject
                {
                    ["status"] = Wire.Name(goal.Standard.Status),
                    ["need"] = Wire.Name(goal.Standard.Finding),
                    ["methods"] = goal.Methods.Count,
                };
            },
            cancellationToken);

    public Task<JsonObject> ProductionLadderAsync(CancellationToken cancellationToken) =>
        ReadAsync(
            "production ladder",
            ct =>
            {
                var review = _store.LoadRounds(ct);
                var ladder = review.Revision == 0 ? null : _store.LoadProductionLadder(WorldOf(review), ct);
                return new JsonObject
                {
                    ["recorded"] = ladder is not null,
                    ["resource"] = ladder?.Resource.Value ?? "",
                    ["bench"] = ladder?.Bench ?? "",
                    ["recipe"] = ladder?.Recipe ?? "",
                    ["research"] = new JsonArray([.. (ladder?.Research ?? []).Select(r => (JsonNode?)r)]),
                };
            },
            cancellationToken);

    public Task<IReadOnlyList<JsonObject>> PlanHistoryAsync(int limit, IReadOnlyList<string> methodPatterns, CancellationToken cancellationToken) =>
        ReadAsync(
            "plan history " + string.Join(',', methodPatterns),
            ct => (IReadOnlyList<JsonObject>)[.. _store.PlanHistoryWithMethods(Math.Clamp(limit, 1, 256), methodPatterns, ct).Select(PlanRow)],
            cancellationToken);

    public Task<(bool Bound, IReadOnlyList<JsonObject> Plans)> ConcernPlansAsync(string concern, CancellationToken cancellationToken) =>
        ReadAsync<(bool, IReadOnlyList<JsonObject>)>(
            "concern plans " + concern,
            ct =>
            {
                var review = _store.LoadRounds(ct);
                if (BoundStandard(review, new ConcernId(concern)) is not { } id)
                {
                    return (false, []);
                }

                var goal = _store.LoadStandard(id, ct);
                var plans = new List<JsonObject>();
                for (ulong epoch = 0; epoch <= goal.Standard.Episode; epoch++)
                {
                    foreach (var method in _store.LoadMethods(id, epoch, ct))
                    {
                        var row = PlanRow(_store.LoadPlan(method.Plan, ct), method.MethodId.Value);
                        row["concern"] = id.Value;
                        row["epoch"] = (long)epoch;
                        plans.Add(row);
                    }
                }

                return (true, plans);
            },
            cancellationToken);

    public Task<JsonObject?> RecoveryEntryAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync(
            "recovery entry " + id,
            ct =>
            {
                var entry = _store.LoadRounds(ct).RecoveryQueue?.Entries.FirstOrDefault(e => e.ID == id);
                return entry is null
                    ? null
                    : new JsonObject
                    {
                        ["id"] = entry.ID,
                        ["status"] = Wire.Name(entry.Status),
                        ["tier"] = Wire.Name(entry.Tier),
                        ["short"] = new JsonArray([.. entry.Short.Select(r => (JsonNode?)r.Value)]),
                        ["reason"] = entry.Reason,
                    };
            },
            cancellationToken);

    public Task<IReadOnlyList<string>> ShrineHoldsAsync(CancellationToken cancellationToken) =>
        ReadAsync(
            "shrine holds",
            ct => (IReadOnlyList<string>)[.. _store.LoadRounds(ct).ShrineHolds.Select(h => $"{h.Shrine}:{h.Reason}:{h.Wall}")],
            cancellationToken);

    async Task<(ulong Revision, ulong Tick)?> IPhaseStore.LoadRoundsAsync(CancellationToken cancellationToken)
    {
        var review = await ReadAsync("read rounds", ct => _store.LoadRounds(ct), cancellationToken).ConfigureAwait(false);
        return review.Revision == 0 ? null : (review.Revision, (ulong)review.Tick.Value);
    }

    public Task<LayoutPlanRecord?> LayoutPlanAsync(ulong reviewTick, CancellationToken cancellationToken) =>
        ReadAsync(
            "layout plan",
            ct =>
            {
                var review = _store.LoadRounds(ct);
                if (review.Revision == 0 || _store.LayoutPlan(review.Snapshot, new Tick((long)reviewTick), ct) is not { Plan: { } plan } found)
                {
                    return null;
                }

                return new LayoutPlanRecord((ulong)found.Tick.Value, JsonNode.Parse(StoreJson.Serialize(plan)) as JsonObject ?? []);
            },
            cancellationToken);

    /// <summary>The layout plan the journal recorded for its latest persisted review, and that review's tick.</summary>
    internal static async Task<(ulong Tick, LayoutPlan? Plan)> ReadLayoutPlanAsync(Config config, CancellationToken cancellationToken)
    {
        using var store = await OpenAsync(config, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(
            "layout plan",
            ct =>
            {
                var review = store._store.LoadRounds(ct);
                if (review.Revision == 0)
                {
                    return (0UL, (LayoutPlan?)null);
                }

                var plan = store._store.LayoutPlan(review.Snapshot, review.Tick, ct)?.Plan;
                return ((ulong)review.Tick.Value, plan);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The brain's combat_fights table.</summary>
    internal static async Task<ICombatFightJournal> OpenCombatFightsAsync(Config config, CancellationToken cancellationToken) =>
        new CombatFights(await OpenAsync(config, cancellationToken).ConfigureAwait(false));

    private sealed class CombatFights(CaseStore owner) : ICombatFightJournal, IDisposable
    {
        public Task<IReadOnlyDictionary<string, CombatFightRow>> OpenCombatFightsAsync(CancellationToken cancellationToken) =>
            ReadAsync(
                "open combat fights",
                ct => (IReadOnlyDictionary<string, CombatFightRow>)owner._store.OpenCombatFights(ct).ToDictionary(f => f.Key.Value, f => Row(f.Value), StringComparer.Ordinal),
                cancellationToken);

        public Task<CombatFightRow?> LoadCombatFightAsync(string plan, CancellationToken cancellationToken) =>
            ReadAsync("combat fight " + plan, ct => owner._store.LoadCombatFight(new PlanId(plan), ct) is { } fight ? Row(fight) : null, cancellationToken);

        public void Dispose() => owner.Dispose();

        private static CombatFightRow Row(CombatFight fight) => new(fight.Open, fight.Roster.Select(p => p.Value).ToHashSet(StringComparer.Ordinal));
    }

    private static async Task<T> ReadAsync<T>(string what, Func<CancellationToken, T> read, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => read(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not AcceptanceException)
        {
            throw new AcceptanceException($"journal {what}: {e.Message}", e);
        }
    }

    private static StandardId? BoundStandard(ReviewRecord review, ConcernId concern)
    {
        foreach (var binding in review.Standards)
        {
            if (binding.Concern == concern)
            {
                return binding.Standard;
            }
        }

        return null;
    }

    private static World WorldOf(ReviewRecord review) => new(review.Snapshot.Colony, review.Snapshot.Load, review.Snapshot.Map);

    private JsonObject SampleStandard(ConcernId need, CancellationToken ct)
    {
        var sample = new JsonObject { ["at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["method_count"] = 0 };
        var review = _store.LoadRounds(ct);
        sample["review_revision"] = (long)review.Revision;
        sample["review_tick"] = review.Tick.Value;
        foreach (var progress in review.Progress)
        {
            if (progress.Concern == need)
            {
                sample["blocked"] = progress.Blocked.Value;
            }
        }

        sample["latch_food"] = review.Latches.Food;
        if (PolicyFunctions.IsIncidentKind(need))
        {
            return SampleIncident(review, need, sample, ct);
        }

        if (BoundStandard(review, need) is not { } standard)
        {
            sample["concern_bound"] = false;
            return sample;
        }

        sample["concern_bound"] = true;
        StandardState goal;
        try
        {
            goal = _store.LoadStandard(standard, ct);
        }
        catch (NotFoundException)
        {
            return sample;
        }

        sample["status"] = Wire.Name(goal.Standard.Status);
        sample["need"] = Wire.Name(goal.Standard.Finding);
        sample["vetoed"] = review.Veto(goal.Standard).Length != 0;
        sample["epoch"] = (long)goal.Standard.Episode;
        sample["method_count"] = goal.Methods.Count;
        var plans = new JsonArray();
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in goal.Methods)
        {
            active.Add(method.Plan.Value);
            plans.Add(DescribeMethod(method.MethodId, method.Plan, ct));
        }

        sample["plans"] = plans;
        var retired = new JsonArray();
        try
        {
            foreach (var method in _store.LoadMethods(standard, goal.Standard.Episode, ct))
            {
                if (!active.Contains(method.Plan.Value))
                {
                    retired.Add(DescribeMethod(method.MethodId, method.Plan, ct));
                }
            }
        }
        catch (Exception e) when (e is DomainException or NotFoundException)
        {
            _ = e;
        }

        sample["retired_plans"] = retired;
        return sample;
    }

    private JsonObject SampleIncident(ReviewRecord review, ConcernId kind, JsonObject sample, CancellationToken ct)
    {
        var incident = review.Revision == 0 ? null : _store.LatestIncident(WorldOf(review), kind, ct);
        if (incident is null)
        {
            sample["concern_bound"] = false;
            return sample;
        }

        var binding = review.Incident(kind);
        sample["concern_bound"] = binding is not null;
        sample["incident"] = incident.Incident.Id.Value;
        sample["status"] = incident.Incident.Closed ? "closed" : "open";
        sample["need"] = binding is null ? "" : Wire.Name(binding.Situation);
        sample["vetoed"] = review.VetoIncident(incident.Incident).Length != 0;
        sample["method_count"] = incident.Methods.Count;
        var plans = new JsonArray();
        foreach (var method in incident.Methods)
        {
            var described = DescribeMethod(method.Method, method.Plan, ct);
            described.Remove("kinds");
            plans.Add(described);
        }

        sample["plans"] = plans;
        return sample;
    }

    private JsonObject DescribeMethod(MethodId method, PlanId plan, CancellationToken ct)
    {
        PlanState state;
        try
        {
            state = _store.LoadPlan(plan, ct);
        }
        catch (Exception e) when (e is DomainException or NotFoundException)
        {
            return new JsonObject { ["plan"] = plan.Value, ["error"] = e.Message };
        }

        var stages = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unsuccessful = new JsonArray();
        foreach (var progress in state.Progress)
        {
            var stage = Wire.Name(progress.View.Stage);
            stages[stage] = stages.GetValueOrDefault(stage) + 1;
            var kind = Wire.Name(progress.Action.Kind);
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            if (progress.View.Stage == Stage.Unsuccessful)
            {
                unsuccessful.Add(new JsonObject { ["action"] = progress.View.Action.Value, ["kind"] = kind, ["reason"] = Wire.Name(progress.View.UnsuccessfulCause()) });
            }
        }

        var described = new JsonObject
        {
            ["plan"] = plan.Value,
            ["method"] = method.Value,
            ["actions"] = state.Spec.Actions.Count,
            ["stages"] = Counts(stages),
            ["kinds"] = Counts(kinds),
        };
        if (unsuccessful.Count > 0)
        {
            described["unsuccessful"] = unsuccessful;
        }

        return described;
    }

    private static JsonObject Counts(SortedDictionary<string, int> counts)
    {
        var obj = new JsonObject();
        foreach (var (key, count) in counts)
        {
            obj[key] = count;
        }

        return obj;
    }

    private static JsonObject PlanRow(PlanState plan) => PlanRow(plan, plan.Method.Value);

    private static JsonObject PlanRow(PlanState plan, string method) => new()
    {
        ["plan"] = plan.Spec.Id.Value,
        ["method"] = method,
        ["steps"] = new JsonArray([.. plan.Progress.Select(p => (JsonNode?)new JsonObject
        {
            ["id"] = p.Action.Id.Value,
            ["kind"] = Wire.Name(p.Action.Kind),
            ["stage"] = Wire.Name(p.View.Stage),
            ["action"] = Payload(p.Action),
        })]),
    };

    private static JsonObject ProgressRow(Progress progress)
    {
        var payload = Payload(progress.Action);
        var kind = progress.Action.Kind;
        var row = new JsonObject
        {
            ["kind"] = Wire.Name(kind),
            ["stage"] = Wire.Name(progress.View.Stage),
            ["tick"] = progress.View.Tick.Value,
        };
        switch (kind)
        {
            case ActionKind.ProductionBill:
                row["mode"] = payload["mode"]?.DeepClone();
                break;
            case ActionKind.PolicyPrune:
                row["database"] = payload["database"]?.DeepClone();
                break;
            case ActionKind.Husbandry:
                row["method"] = payload["method"]?.DeepClone();
                row["animal"] = payload["animal"]?.DeepClone();
                row["argument"] = payload["argument"]?.DeepClone();
                break;
            case ActionKind.CutPlant:
                row["plant"] = payload["plant"]?.DeepClone();
                row["receipt"] = progress.View.Receipt.ValueOrDefault == Receipt.Accepted ? Wire.Name(Receipt.Accepted) : "";
                break;
        }

        return row;
    }

    private static JsonObject Payload(DomainAction action) =>
        JsonNode.Parse(StoreJson.Serialize(action)) is JsonObject written && Camel(written["Payload"]) is JsonObject payload ? payload : [];

    private static JsonNode? Camel(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(char.ToLowerInvariant(p.Key[0]) + p.Key[1..], Camel(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Camel)]),
        null => null,
        _ => node.DeepClone(),
    };
}
