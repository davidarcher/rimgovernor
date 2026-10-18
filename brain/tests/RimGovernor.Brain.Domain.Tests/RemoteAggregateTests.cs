using System.Text;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
using RimGovernor.Brain.RemoteAccept;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Remote aggregation accepts the evidence its own run wrote (#3536): the planner's selection replays to the same shards,
/// and the suite's attempt rows carry timestamps the aggregate reads.
/// </summary>
public sealed class RemoteAggregateTests
{
    // run.json of nightly run 39118167812, whose aggregate rejected its own selection.
    private const string NightlyRun = """
        {
          "tested_commit": "2290aa7c7a6d2a9b9f0a4642aa3270339d61d794",
          "cases": [],
          "run_id": "gh:davidarcher/rimgovernor:39118167812:1",
          "tier": "nightly",
          "schema_version": 1,
          "bundle": { "path": "bundle.json", "sha256": "1f1a320d374bf4cbe98bc7f907cf34d03e2c8fb89099bdb53cbe913b3b3d108e" },
          "repository": "davidarcher/rimgovernor",
          "workflow_commit": "2290aa7c7a6d2a9b9f0a4642aa3270339d61d794",
          "trigger": {
            "published_ref": "refs/heads/main",
            "actor": "davidarcher",
            "actions_run_attempt": 1,
            "actions_run_id": 39118167812,
            "event": "workflow_dispatch"
          },
          "limits": {
            "shards": 32,
            "artifact_max_bytes": 0,
            "suite_timeout_minutes": 345,
            "max_parallel": 20,
            "paid_usage_authorized": false,
            "max_attempts": 1,
            "job_timeout_minutes": 360,
            "artifact_retention_days": 7,
            "runner_label": "windows-2022",
            "workers_per_shard": 1
          }
        }
        """;

    // attempts.json of cases run 39118419674 shard s1 without its attempts, which the test fills from the suite writer.
    private const string CasesShard = """
        {
          "schema_version": 1,
          "run": { "path": "run.json", "sha256": "dad7732a648d513bd592147df11c50bdf9161f0d7145c90781032f3e40493498" },
          "selection": { "path": "selection.json", "sha256": "d8a71432bfe0e67d30b3cf2a62ee2dbb73f6fb6747fe96757a8b652e97eb852c" },
          "shard_id": "s1",
          "runner": {
            "os": "windows",
            "image_version": "20261012.333.1",
            "arch": "x64",
            "cpu_count": 4,
            "memory_bytes": 17174360064,
            "free_disk_bytes": 84329938944,
            "bootstrap": { "path": "s1/fixture/bootstrap.json", "sha256": "a1d530c6355293ce39482a1f035de09fb7b1bd0688001c85174bda2f5bc22a13" }
          },
          "attempts": []
        }
        """;

    [Fact]
    public void NightlySelectionReplaysToItsOwnShards()
    {
        var registry = new CaseRegistry();
        CaseCatalog.Register(registry);
        var raw = Encoding.UTF8.GetBytes(NightlyRun);
        var run = PlanCommand.DecodePlanRun(raw);

        var written = PlanCommand.BuildSelection(run, new PlanReference("run.json", new string('a', 64)), registry);
        var selection = Selection.Parse(JsonNode.Parse(written.ToJsonString()));

        Assert.True(RemoteEvidence.ReplaysShards(selection, run.Limits.Shards));
    }

    [Fact]
    public void SuiteAttemptsCarryTimestampsTheAggregateReads()
    {
        var started = DateTimeOffset.UtcNow;
        var failed = new SuiteAttempt
        {
            Case = "production/clothing-activity",
            Number = 1,
            Status = "failed",
            Classification = "infrastructure",
            StartedAt = started,
            FinishedAt = started.AddMinutes(2),
            Exit = 1,
            Error = "game exited",
            Evidence = new EvidenceReference("s1/fixture/production/clothing-activity/result.json", new string('b', 64)),
        };
        var retried = failed with
        {
            Number = 2,
            Status = "passed",
            Classification = "none",
            RetryOf = 1,
            StartedAt = started.AddMinutes(2),
            FinishedAt = started.AddMinutes(4),
            Exit = 0,
            Error = null,
        };
        var shard = JsonNode.Parse(CasesShard)!.AsObject();
        shard["attempts"] = new JsonArray(failed.ToJson(), retried.ToJson());

        var items = Attempts.Parse(JsonNode.Parse(shard.ToJsonString())).Items;

        RemoteEvidence.ValidateAttempt(items[0], null, false, 2);
        RemoteEvidence.ValidateAttempt(items[1], items[0], true, 2);
    }
}
