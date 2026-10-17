using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Brain.RemoteAccept;

// The remote acceptance v1 evidence graph (Go internal/remoteaccept types.go). Property names are the Go JSON tags: the
// shell scripts, the workflow and previously written evidence use them. Reading is strict (StrictJson), writing is by hand.

/// <summary>A digest-pinned reference to a file under the evidence root.</summary>
public sealed record Ref(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256)
{
    internal static Ref Parse(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new RemoteAcceptException("null or malformed reference");
        return new Ref(StrictJson.Str(o, "path"), StrictJson.Str(o, "sha256"));
    }

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("path", Path);
        w.WriteString("sha256", Sha256);
        w.WriteEndObject();
    }
}

/// <summary>What fired the Actions run.</summary>
public sealed record RunTrigger(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("published_ref")] string PublishedRef,
    [property: JsonPropertyName("actions_run_id")] long RunId,
    [property: JsonPropertyName("actions_run_attempt")] int Attempt);

/// <summary>The run's resource limits.</summary>
public sealed record RunLimits(
    [property: JsonPropertyName("runner_label")] string Runner,
    [property: JsonPropertyName("shards")] int Shards,
    [property: JsonPropertyName("max_parallel")] int Parallel,
    [property: JsonPropertyName("workers_per_shard")] int Workers,
    [property: JsonPropertyName("job_timeout_minutes")] int JobMinutes,
    [property: JsonPropertyName("suite_timeout_minutes")] int SuiteMinutes,
    [property: JsonPropertyName("max_attempts")] int Attempts,
    [property: JsonPropertyName("artifact_retention_days")] int Retention,
    [property: JsonPropertyName("artifact_max_bytes")] long Bytes,
    [property: JsonPropertyName("paid_usage_authorized")] bool Paid);

/// <summary>run.json: the identity of one remote acceptance run.</summary>
public sealed record Run(
    [property: JsonPropertyName("schema_version")] int Version,
    [property: JsonPropertyName("run_id")] string Id,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("trigger")] RunTrigger Trigger,
    [property: JsonPropertyName("workflow_commit")] string WorkflowCommit,
    [property: JsonPropertyName("tested_commit")] string TestedCommit,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("bundle")] Ref Bundle,
    [property: JsonPropertyName("limits")] RunLimits Limits)
{
    internal static Run Parse(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new RemoteAcceptException("null or malformed run");
        var t = StrictJson.Obj(o, "trigger");
        var l = StrictJson.Obj(o, "limits");
        return new Run(
            StrictJson.Int(o, "schema_version"),
            StrictJson.Str(o, "run_id"),
            StrictJson.Str(o, "repository"),
            new RunTrigger(StrictJson.Str(t, "event"), StrictJson.Str(t, "actor"), StrictJson.Str(t, "published_ref"), StrictJson.Int64(t, "actions_run_id"), StrictJson.Int(t, "actions_run_attempt")),
            StrictJson.Str(o, "workflow_commit"),
            StrictJson.Str(o, "tested_commit"),
            StrictJson.Str(o, "tier"),
            Ref.Parse(StrictJson.Obj(o, "bundle")),
            new RunLimits(
                StrictJson.Str(l, "runner_label"),
                StrictJson.Int(l, "shards"),
                StrictJson.Int(l, "max_parallel"),
                StrictJson.Int(l, "workers_per_shard"),
                StrictJson.Int(l, "job_timeout_minutes"),
                StrictJson.Int(l, "suite_timeout_minutes"),
                StrictJson.Int(l, "max_attempts"),
                StrictJson.Int(l, "artifact_retention_days"),
                StrictJson.Int64(l, "artifact_max_bytes"),
                StrictJson.Bool(l, "paid_usage_authorized")));
    }
}

/// <summary>One selected case with the reasons it is selected.</summary>
public sealed record SelectedCase(
    [property: JsonPropertyName("budget_ns")] long BudgetNs,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);

/// <summary>selection.json: the planned cases and their shards.</summary>
public sealed record Selection(
    [property: JsonPropertyName("schema_version")] int Version,
    [property: JsonPropertyName("run")] Ref Run,
    [property: JsonPropertyName("planner_commit")] string Planner,
    [property: JsonPropertyName("cases")] IReadOnlyList<SelectedCase> Cases,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("shards")] IReadOnlyList<PlannedShard> Shards)
{
    internal static Selection Parse(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new RemoteAcceptException("null or malformed selection");
        return new Selection(
            StrictJson.Int(o, "schema_version"),
            Ref.Parse(StrictJson.Obj(o, "run")),
            StrictJson.Str(o, "planner_commit"),
            StrictJson.List(o, "cases", item =>
            {
                var c = StrictJson.ElementObj(item);
                return new SelectedCase(
                    StrictJson.OptInt64(c, "budget_ns"),
                    StrictJson.Str(c, "name"),
                    StrictJson.List(c, "reasons", StrictJson.ElementStr));
            }),
            StrictJson.Str(o, "algorithm"),
            StrictJson.List(o, "shards", item =>
            {
                var s = StrictJson.ElementObj(item);
                return new PlannedShard(StrictJson.Str(s, "id"), StrictJson.List(s, "cases", StrictJson.ElementStr));
            }));
    }
}

/// <summary>One attempt at one case.</summary>
public sealed record Attempt(
    [property: JsonPropertyName("case")] string Case,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("retry_of")] int? RetryOf,
    [property: JsonPropertyName("started_at")] string Started,
    [property: JsonPropertyName("finished_at")] string Finished,
    [property: JsonPropertyName("exit")] int? Exit,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("evidence")] Ref Evidence,
    [property: JsonPropertyName("log")] string? Log)
{
    /// <summary>Gets the raw JSON text of the optional <c>log</c> field (Go <c>json.RawMessage</c>), or null when it is absent.</summary>
    internal static Attempt Parse(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new RemoteAcceptException("null or malformed attempt");
        string? log = null;
        if (o.TryGetPropertyValue("log", out var raw))
        {
            log = raw is null ? "null" : raw.ToJsonString();
        }

        return new Attempt(
            StrictJson.Str(o, "case"),
            StrictJson.Int(o, "number"),
            StrictJson.Str(o, "status"),
            StrictJson.Str(o, "classification"),
            StrictJson.NullableInt(o, "retry_of"),
            StrictJson.Str(o, "started_at"),
            StrictJson.Str(o, "finished_at"),
            StrictJson.NullableInt(o, "exit"),
            StrictJson.NullableStr(o, "error"),
            Ref.Parse(StrictJson.Obj(o, "evidence")),
            log);
    }

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("case", Case);
        w.WriteNumber("number", Number);
        w.WriteString("status", Status);
        w.WriteString("classification", Classification);
        WriteNullableInt(w, "retry_of", RetryOf);
        w.WriteString("started_at", Started);
        w.WriteString("finished_at", Finished);
        WriteNullableInt(w, "exit", Exit);
        if (Error is null)
        {
            w.WriteNull("error");
        }
        else
        {
            w.WriteString("error", Error);
        }

        w.WritePropertyName("evidence");
        Evidence.Write(w);
        if (Log is not null)
        {
            w.WritePropertyName("log");
            StrictJson.WriteRaw(w, Log);
        }

        w.WriteEndObject();
    }

    internal static void WriteNullableInt(Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }
}

/// <summary>The runner a shard ran on.</summary>
public sealed record AttemptsRunner(
    [property: JsonPropertyName("os")] string OS,
    [property: JsonPropertyName("image_version")] string Image,
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("cpu_count")] int Cpus,
    [property: JsonPropertyName("memory_bytes")] long Memory,
    [property: JsonPropertyName("free_disk_bytes")] long Disk,
    [property: JsonPropertyName("bootstrap")] Ref Bootstrap);

/// <summary>shards/&lt;id&gt;/attempts.json: every attempt of every case of one shard.</summary>
public sealed record Attempts(
    [property: JsonPropertyName("schema_version")] int Version,
    [property: JsonPropertyName("run")] Ref Run,
    [property: JsonPropertyName("selection")] Ref Selection,
    [property: JsonPropertyName("shard_id")] string ShardId,
    [property: JsonPropertyName("runner")] AttemptsRunner Runner,
    [property: JsonPropertyName("attempts")] IReadOnlyList<Attempt> Items)
{
    internal static Attempts Parse(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new RemoteAcceptException("null or malformed attempts");
        var r = StrictJson.Obj(o, "runner");
        return new Attempts(
            StrictJson.Int(o, "schema_version"),
            Ref.Parse(StrictJson.Obj(o, "run")),
            Ref.Parse(StrictJson.Obj(o, "selection")),
            StrictJson.Str(o, "shard_id"),
            new AttemptsRunner(
                StrictJson.Str(r, "os"),
                StrictJson.Str(r, "image_version"),
                StrictJson.Str(r, "arch"),
                StrictJson.Int(r, "cpu_count"),
                StrictJson.Int64(r, "memory_bytes"),
                StrictJson.Int64(r, "free_disk_bytes"),
                Ref.Parse(StrictJson.Obj(r, "bootstrap"))),
            StrictJson.List(o, "attempts", Attempt.Parse));
    }

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("schema_version", Version);
        w.WritePropertyName("run");
        Run.Write(w);
        w.WritePropertyName("selection");
        Selection.Write(w);
        w.WriteString("shard_id", ShardId);
        w.WritePropertyName("runner");
        w.WriteStartObject();
        w.WriteString("os", Runner.OS);
        w.WriteString("image_version", Runner.Image);
        w.WriteString("arch", Runner.Arch);
        w.WriteNumber("cpu_count", Runner.Cpus);
        w.WriteNumber("memory_bytes", Runner.Memory);
        w.WriteNumber("free_disk_bytes", Runner.Disk);
        w.WritePropertyName("bootstrap");
        Runner.Bootstrap.Write(w);
        w.WriteEndObject();
        w.WritePropertyName("attempts");
        w.WriteStartArray();
        foreach (var a in Items)
        {
            a.Write(w);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}

/// <summary>One shard's entry in the aggregate: its status and the reference to its attempts.json.</summary>
public sealed record Shard(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("attempts")] Ref? Attempts)
{
    internal static Shard Parse(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new RemoteAcceptException("null or malformed shard");
        var attempts = StrictJson.NullableObj(o, "attempts");
        return new Shard(StrictJson.Str(o, "id"), StrictJson.Str(o, "status"), attempts is null ? null : Ref.Parse(attempts));
    }

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteString("status", Status);
        w.WritePropertyName("attempts");
        if (Attempts is null)
        {
            w.WriteNullValue();
        }
        else
        {
            Attempts.Write(w);
        }

        w.WriteEndObject();
    }
}

/// <summary>One case's row in the aggregate.</summary>
public sealed record CaseRow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shard_id")] string ShardId,
    [property: JsonPropertyName("attempt_count")] int AttemptCount,
    [property: JsonPropertyName("final_attempt")] int? FinalAttempt,
    [property: JsonPropertyName("status")] string Status)
{
    internal static CaseRow Parse(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new RemoteAcceptException("null or malformed case");
        return new CaseRow(
            StrictJson.Str(o, "name"),
            StrictJson.Str(o, "shard_id"),
            StrictJson.Int(o, "attempt_count"),
            StrictJson.NullableInt(o, "final_attempt"),
            StrictJson.Str(o, "status"));
    }

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("name", Name);
        w.WriteString("shard_id", ShardId);
        w.WriteNumber("attempt_count", AttemptCount);
        Attempt.WriteNullableInt(w, "final_attempt", FinalAttempt);
        w.WriteString("status", Status);
        w.WriteEndObject();
    }
}

/// <summary>aggregate.json: the recomputed verdict over all shards.</summary>
public sealed record Aggregate(
    [property: JsonPropertyName("schema_version")] int Version,
    [property: JsonPropertyName("run")] Ref Run,
    [property: JsonPropertyName("selection")] Ref Selection,
    [property: JsonPropertyName("shards")] IReadOnlyList<Shard> Shards,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("cases")] IReadOnlyList<CaseRow> Cases,
    [property: JsonPropertyName("error")] string? Error)
{
    internal static Aggregate Parse(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new RemoteAcceptException("null or malformed aggregate");
        return new Aggregate(
            StrictJson.Int(o, "schema_version"),
            Ref.Parse(StrictJson.Obj(o, "run")),
            Ref.Parse(StrictJson.Obj(o, "selection")),
            StrictJson.List(o, "shards", Shard.Parse),
            StrictJson.Str(o, "status"),
            StrictJson.Bool(o, "passed"),
            StrictJson.List(o, "cases", CaseRow.Parse),
            StrictJson.NullableStr(o, "error"));
    }

    /// <summary>Field-by-field equality (Go <c>reflect.DeepEqual</c>); lists compare by content.</summary>
    internal bool SameAs(Aggregate other) =>
        Version == other.Version && Run == other.Run && Selection == other.Selection && Status == other.Status
        && Passed == other.Passed && Error == other.Error
        && Shards.SequenceEqual(other.Shards) && Cases.SequenceEqual(other.Cases);

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("schema_version", Version);
        w.WritePropertyName("run");
        Run.Write(w);
        w.WritePropertyName("selection");
        Selection.Write(w);
        w.WritePropertyName("shards");
        w.WriteStartArray();
        foreach (var s in Shards)
        {
            s.Write(w);
        }

        w.WriteEndArray();
        w.WriteString("status", Status);
        w.WriteBoolean("passed", Passed);
        w.WritePropertyName("cases");
        w.WriteStartArray();
        foreach (var c in Cases)
        {
            c.Write(w);
        }

        w.WriteEndArray();
        if (Error is null)
        {
            w.WriteNull("error");
        }
        else
        {
            w.WriteString("error", Error);
        }

        w.WriteEndObject();
    }
}

/// <summary>The operator-supplied trust pins; read from local git config, never from artifact contents.</summary>
public sealed record Trust(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("workflow")] string Workflow,
    [property: JsonPropertyName("workflow_commit")] string WorkflowCommit,
    [property: JsonPropertyName("bundle_sha256")] string BundleSha256);

/// <summary>The authenticated Actions artifact an import came from.</summary>
public sealed record Provenance(
    [property: JsonPropertyName("trust")] Trust Trust,
    [property: JsonPropertyName("artifact_id")] long ArtifactId,
    [property: JsonPropertyName("archive_sha256")] string ArchiveSha256,
    [property: JsonPropertyName("run_id")] long RunId,
    [property: JsonPropertyName("attempt")] int Attempt)
{
    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WritePropertyName("trust");
        w.WriteStartObject();
        w.WriteString("repository", Trust.Repository);
        w.WriteString("workflow", Trust.Workflow);
        w.WriteString("workflow_commit", Trust.WorkflowCommit);
        w.WriteString("bundle_sha256", Trust.BundleSha256);
        w.WriteEndObject();
        w.WriteNumber("artifact_id", ArtifactId);
        w.WriteString("archive_sha256", ArchiveSha256);
        w.WriteNumber("run_id", RunId);
        w.WriteNumber("attempt", Attempt);
        w.WriteEndObject();
    }
}

/// <summary>The remote block of a result report.</summary>
public sealed record Remote(
    [property: JsonPropertyName("schema_version")] int Version,
    [property: JsonPropertyName("aggregate")] Ref? Aggregate,
    [property: JsonPropertyName("tested_commit")] string TestedCommit,
    [property: JsonPropertyName("bundle_sha256")] string BundleSha256,
    [property: JsonPropertyName("import")] Provenance? Import)
{
    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("schema_version", Version);
        w.WritePropertyName("aggregate");
        (Aggregate ?? new Ref("", "")).Write(w);
        w.WriteString("tested_commit", TestedCommit);
        w.WriteString("bundle_sha256", BundleSha256);
        if (Import is not null)
        {
            w.WritePropertyName("import");
            Import.Write(w);
        }

        w.WriteEndObject();
    }
}

/// <summary>result.json: the native-report shape <c>land</c> reads, one untouched native row per case.</summary>
public sealed record Report(
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("cases")] IReadOnlyList<JsonObject> Cases,
    [property: JsonPropertyName("remote")] Remote Remote)
{
    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("tier", Tier);
        w.WriteBoolean("passed", Passed);
        if (Error is null)
        {
            w.WriteNull("error");
        }
        else
        {
            w.WriteString("error", Error);
        }

        w.WritePropertyName("cases");
        w.WriteStartArray();
        foreach (var c in Cases)
        {
            StrictJson.WriteSorted(w, c);
        }

        w.WriteEndArray();
        w.WritePropertyName("remote");
        Remote.Write(w);
        w.WriteEndObject();
    }
}
