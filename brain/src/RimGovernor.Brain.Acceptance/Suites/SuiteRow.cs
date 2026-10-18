using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One case row of a suite report (#3410: typed instead of a JSON object the run and the retry wrapper edited by key).
/// <see cref="ToJson"/> writes the row with the keys it always had: an unset optional field is left out, and the fields a
/// reader tells apart as absent or null (<c>exit</c>, <c>game_reuse</c>, <c>final_attempt</c>) keep both forms.
/// </summary>
internal sealed class SuiteRow
{
    public string? Name { get; set; }

    public int? Worker { get; set; }

    /// <summary>The case's output directory under the suite's.</summary>
    public string? Output { get; set; }

    public IReadOnlyList<string>? Argv { get; set; }

    public bool? Serve { get; set; }

    public bool Passed { get; set; }

    public string? Acceptance { get; set; }

    /// <summary>Whether the row records an exit at all; <see cref="Exit"/> null then means the child never started.</summary>
    public bool ExitRecorded { get; private set; }

    public int? Exit { get; private set; }

    public long? WallMs { get; set; }

    /// <summary>The child's captured output.</summary>
    public string? Log { get; set; }

    public string? Error { get; set; }

    /// <summary>Whether the child's result.json was read; <see cref="GameReuse"/> is then written even when null.</summary>
    public bool ResultRead { get; private set; }

    public JsonNode? GameReuse { get; private set; }

    public long? BootMs { get; set; }

    public Metrics? Metrics { get; set; }

    public JsonObject? World { get; set; }

    public Flake? Flake { get; set; }

    public IReadOnlyList<DriftFlag>? Drift { get; set; }

    public string? RetryCleanupError { get; set; }

    public string? RetryPolicy { get; set; }

    /// <summary>Every attempt of the row once the retry wrapper finished it; null before.</summary>
    public IReadOnlyList<SuiteAttempt>? Attempts { get; set; }

    public string? Disposition { get; set; }

    /// <summary>Records the child's exit status; null for a child that never started.</summary>
    public void RecordExit(int? code)
    {
        ExitRecorded = true;
        Exit = code;
    }

    /// <summary>Folds the run's own result.json into the row: the verdict, and the blocks a suite report carries per case.</summary>
    public void ApplyResult(CaseResult result, bool runFailed)
    {
        ArgumentNullException.ThrowIfNull(result);
        Passed = result.Passed == true && !runFailed;
        if (result.Error is { Length: > 0 } error)
        {
            Error = error;
        }

        ResultRead = true;
        GameReuse = result.GameReuse;
        if (result.BootMs is { } boot)
        {
            BootMs = (long)boot;
        }

        Metrics = Metrics.Of(result.Metrics);
        World = result.World;
        Flake = result.Flake;
        if (result.Drift is { Count: > 0 } drift)
        {
            Drift = drift;
        }
    }

    /// <summary>The row as the suite report writes it.</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject();
        row.AddIfPresent("name", Name)
            .AddIfPresent("worker", Worker)
            .AddIfPresent("output", Output)
            .AddIfPresent("argv", Argv is null ? null : OutputJson.Array(Argv))
            .AddIfPresent("serve", Serve);
        row.Add("passed", Passed);
        row.AddIfPresent("acceptance", Acceptance);
        if (ExitRecorded)
        {
            row.Add("exit", Exit);
        }

        row.AddIfPresent("wall_ms", WallMs)
            .AddIfPresent("log", Log)
            .AddIfPresent("error", Error);
        if (ResultRead)
        {
            row.Add("game_reuse", GameReuse?.DeepClone());
        }

        row.AddIfPresent("boot_ms", BootMs)
            .AddIfPresent(Metrics.MetricsKey, Metrics?.ToJson())
            .AddIfPresent("world", World?.DeepClone())
            .AddIfPresent("flake", Flake?.ToJson())
            .AddIfPresent("drift", Drift is null ? null : OutputJson.Array(Drift, flag => flag.ToJson()))
            .AddIfPresent("retry_cleanup_error", RetryCleanupError)
            .AddIfPresent("retry_policy", RetryPolicy);
        if (Attempts is not null)
        {
            row.Add("attempts", OutputJson.Array(Attempts, attempt => attempt.ToJson()));
            row.Add("attempt_count", Attempts.Count);
            row.Add("final_attempt", Attempts.Count > 0 ? Attempts.Count : null);
        }

        return row.AddIfPresent("disposition", Disposition);
    }
}

/// <summary>One attempt at a suite row: the record remote evidence keeps per attempt.</summary>
internal sealed record SuiteAttempt
{
    public string Case { get; init; } = "";

    public int Number { get; init; }

    /// <summary>passed, failed or cancelled.</summary>
    public string Status { get; init; } = "failed";

    public string Classification { get; init; } = "";

    public int? RetryOf { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset FinishedAt { get; init; }

    public int? Exit { get; init; }

    public string? Error { get; init; }

    /// <summary>The attempt's result.json; null when it is not there.</summary>
    public EvidenceReference? Evidence { get; init; }

    /// <summary>The attempt's captured log; null when it is not there.</summary>
    public EvidenceReference? Log { get; init; }

    public JsonObject ToJson() => new()
    {
        { "case", Case },
        { "number", Number },
        { "status", Status },
        { "classification", Classification },
        { "retry_of", RetryOf },
        { "started_at", StartedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
        { "finished_at", FinishedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
        { "exit", Exit },
        { "error", Error },
        { "evidence", Evidence?.ToJson() },
        { "log", Log?.ToJson() },
    };
}

/// <summary>An immutable reference to a file under the suite output: its slash path relative to the output and its SHA-256.</summary>
internal sealed record EvidenceReference(string Path, string Sha256)
{
    public JsonObject ToJson() => new() { { "path", Path }, { "sha256", Sha256 } };
}
