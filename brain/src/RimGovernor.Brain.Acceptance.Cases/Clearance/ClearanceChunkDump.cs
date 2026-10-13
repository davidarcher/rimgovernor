using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clearance/chunk-dump</c> (Go <c>clearance.go</c>): routine Home clearance of three fixture chunks; exact native
/// targets, journal holds and observed completion. ancient_wall, roof_support_refused and standing_designation replay as
/// colony snapshots instead (internal/snapshot, #746).
/// </summary>
internal static class ClearanceChunkDump
{
    private const string FixtureKey = "clearance_fixture";
    private const string Stage = "clearance-ready";
    private const string Scenario = "chunk_dump";

    internal static AcceptanceCase Case() => new()
    {
        Name = "clearance/chunk-dump",
        Scope = "Routine Home clearance: " + Scenario + "; exact native targets, journal holds and observed completion.",
        Start = new SaveStart { Name = "RimGovernor-tribal8-baseline" },
        RequiredOps = ["test/clearance_prepare", "test/clearance_support", "test/clearance_audit"],
        Serve = new ServeSpec { Families = ["clearance", "tend", "rescue"], Prefix = "clearance" },
        Stages = [Stage],
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Stall = TimeSpan.FromSeconds(60),
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        JsonObject? fixture = null;
        await s.StageAsync(
            Stage,
            async ct =>
            {
                fixture = await s.Harness.CallAsync("prepare-clearance", "test/clearance_prepare", new JsonObject { ["scenario"] = Scenario }, ct).ConfigureAwait(false);
                Checkpoints.SetState(FixtureKey, fixture.DeepClone());
            },
            cancellationToken).ConfigureAwait(false);
        if (StagePlanning.RestoredState(s.Resumed, s.Staged, FixtureKey).AsMap() is { } restored)
        {
            fixture = restored;
        }

        if (fixture is null)
        {
            throw new AcceptanceException("missing staged clearance identities");
        }

        s.Report["fixture"] = fixture.DeepClone();
        var before = await ClearanceKit.CensusAsync(s, "before", cancellationToken).ConfigureAwait(false);
        s.Report["census_before"] = before.DeepClone();
        await RunChunksAsync(s, fixture, before, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunChunksAsync(ISession s, JsonObject fixture, JsonObject before, CancellationToken cancellationToken)
    {
        var ids = fixture["chunks"].AsSlice();
        if (ids.Count != 3)
        {
            throw new AcceptanceException("expected three fixture chunks");
        }

        foreach (var id in ids)
        {
            var found = false;
            foreach (var raw in before["chunks"].AsSlice())
            {
                var row = raw.AsMap() ?? [];
                if (row["entityId"].AsString() == id.AsString())
                {
                    found = true;
                    if (Flag(row["stored"]) || Flag(row["forbidden"]) || Flag(row["destination"]))
                    {
                        throw new AcceptanceException($"chunk is already stored or forbidden: {row.ToJson()}");
                    }
                }
            }

            if (!found)
            {
                throw new AcceptanceException($"chunk {id.AsString()} absent from clearance census");
            }
        }

        var service = await ClearanceKit.StartAsync(s, cancellationToken).ConfigureAwait(false);
        var journal = await ObservedSession.OpenStoreAsync(s.Config, cancellationToken).ConfigureAwait(false);
        try
        {
            await ClearanceKit.ServiceWait(service).WaitProgressAsync(
                async ct =>
                {
                    var goal = await journal.SampleStandardAsync(PolicyFunctions.ClearHomeObstructions.Value, ct).ConfigureAwait(false);
                    if (!Flag(goal["concern_bound"]) || goal["status"] is null)
                    {
                        return ("waiting for chunk standard", false);
                    }

                    s.Report["chunk_standard"] = goal.DeepClone();

                    // No Haul designation is ever ordered (#2513): the native mod marks chunks always haulable.
                    var need = goal["need"].AsString();
                    return (Wait.Signature(need, goal["method_count"]?.ToJsonString()), need == "met");
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            (journal as IDisposable)?.Dispose();
        }

        _ = await service.StopAsync().ConfigureAwait(false);
        await ClearanceKit.ReattachAsync(s, cancellationToken).ConfigureAwait(false);

        // Standard recovery means a destination exists (the materials yard takes chunks and slag; no dump zone is
        // created). Ordinary native hauling must still move all three items; never equate a store with storage.
        await new Wait { Ceiling = TimeSpan.FromMinutes(1), Stall = TimeSpan.FromSeconds(30) }.WaitProgressAsync(
            async ct =>
            {
                var live = await ClearanceKit.AuditAsync(s, fixture, "chunks_after", ct).ConfigureAwait(false);
                if (CheckDump(live, fixture) is null)
                {
                    return ("stored", true);
                }

                _ = await s.AdvanceAsync(250, null, ct).ConfigureAwait(false);
                return (Wait.Signature(live.ToJsonString()), false);
            },
            cancellationToken).ConfigureAwait(false);

        var after = await ClearanceKit.CensusAsync(s, "chunks-stored", cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            var stored = false;
            foreach (var raw in after["chunks"].AsSlice())
            {
                var row = raw.AsMap() ?? [];
                stored = stored || (row["entityId"].AsString() == id.AsString() && Flag(row["stored"]));
            }

            if (!stored)
            {
                throw new AcceptanceException($"chunk {id.AsString()} is not stored in native clearance census");
            }
        }
    }

    /// <summary>
    /// Asserts the planner made no dumping stockpile and that all three fixture chunks sit in a store whose allow list
    /// takes every fixture kind and steel slag: the materials yard. Returns the failure text, null when it holds.
    /// </summary>
    private static string? CheckDump(JsonObject live, JsonObject fixture)
    {
        var want = new List<string> { "ChunkSlagSteel" };
        want.AddRange(fixture["defs"].AsSlice().Select(raw => raw.AsString()));
        var takes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in live["zones"].AsSlice())
        {
            var zone = raw.AsMap() ?? [];
            var label = zone["label"].AsString();
            if (label == "Dumping")
            {
                return $"a dumping stockpile was created: {zone.ToJson()}";
            }

            var allow = zone["allow"].AsSlice().Select(def => def.AsString()).ToList();
            if (label.Length > 0 && !want.Exists(d => !allow.Contains(d)))
            {
                _ = takes.Add(label);
            }
        }

        var chunks = live["chunks"].AsSlice();
        if (chunks.Count != 3)
        {
            return $"native audit lost chunks: {live["chunks"].ToJson()}";
        }

        foreach (var raw in chunks)
        {
            var chunk = raw.AsMap() ?? [];
            if (!Flag(chunk["stored"]) || !takes.Contains(chunk["zone"].AsString()))
            {
                return $"chunk not hauled to the yard: {chunk.ToJson()} (stores taking [{string.Join(' ', want)}]: [{string.Join(' ', takes)}])";
            }
        }

        return null;
    }
}
