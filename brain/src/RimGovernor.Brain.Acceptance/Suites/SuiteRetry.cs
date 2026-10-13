using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One classification of a failed attempt and whether a retry may follow it (Go <c>retryDecision</c>).</summary>
internal readonly record struct RetryDecision(string Classification, bool Eligible);

/// <summary>
/// The suite's retry wrapper (Go <c>cmd/acceptance/retry.go</c>): it keeps the original row shape as the final projection
/// while retaining immutable evidence references for every attempt. Cleanup failure or cancellation cannot launch a
/// retry. Both attempts share the suite deadline.
/// <para>
/// No captured failure currently establishes an independently transient native read. In particular a read timeout in a
/// failed gameplay report is not proof that the read caused the failure. Adding a rule requires retained evidence and a
/// new policy version. Never classify diagnostic error text for retries.
/// </para>
/// </summary>
internal static class SuiteRetry
{
    /// <summary>The retry policy's version, recorded in the suite report and every row.</summary>
    internal const string RetryPolicy = "native-read-v1-empty";

    /// <summary>Runs one attempt of a row on a worker.</summary>
    internal delegate Task<JsonObject> AttemptRunner(SuiteEntry entry, SuiteOptions opts, string root, int worker, TextWriter stderr, CancellationToken cancellationToken);

    internal delegate RetryDecision RetryClassifier(JsonObject row);

    internal delegate Task RetryCleanup(string root, string gameId, CancellationToken cancellationToken);

    internal static Task<JsonObject> RunEntryAsync(SuiteEntry e, SuiteOptions opts, string root, int worker, TextWriter stderr, CancellationToken cancellationToken) =>
        RunEntryAttemptsAsync(e, opts, root, worker, stderr, SuiteRunner.RunEntryOnceAsync, ClassifyAttempt, StopRetryGameAsync, cancellationToken);

    internal static RetryDecision ClassifyAttempt(JsonObject row) =>
        row["passed"].AsBool() == true ? new RetryDecision("none", false) : new RetryDecision("unknown", false);

    /// <summary>
    /// Stops the root's game and requires a positive confirmation that it is gone, not merely a successful stop request:
    /// neither profile's launch record may still name a live process.
    /// </summary>
    internal static async Task StopRetryGameAsync(string root, string gameId, CancellationToken cancellationToken)
    {
        _ = await Game.StopGameAsync(root, gameId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var profile in new[] { "config-headless", "config" })
        {
            var dir = Path.Combine(root, profile);
            if (Directory.Exists(dir) && HeadlessProfile.GameRunning(dir, gameId))
            {
                throw new AcceptanceException($"retry cleanup did not confirm stopped game: still running under {profile}");
            }
        }
    }

    internal static async Task<JsonObject> RunEntryAttemptsAsync(
        SuiteEntry e,
        SuiteOptions opts,
        string root,
        int worker,
        TextWriter stderr,
        AttemptRunner run,
        RetryClassifier classify,
        RetryCleanup cleanup,
        CancellationToken ctx)
    {
        var attempts = new JsonArray();
        var row = new JsonObject { ["name"] = e.Name, ["passed"] = false };
        long total = 0;
        long boot = 0;
        for (var number = 1; number <= 2; number++)
        {
            if (ctx.IsCancellationRequested)
            {
                row["passed"] = false;
                row["error"] = CancelledText(ctx);
                break;
            }

            var current = opts;
            if (number == 2)
            {
                current = opts with { Resume = false, Output = Path.Combine(opts.Output, "attempts", "2") };
            }

            var started = DateTimeOffset.UtcNow;
            row = await run(e, current, root, worker, stderr, ctx).ConfigureAwait(false);
            var hasExit = row["exit"] is JsonValue exitValue && exitValue.TryGetValue<int>(out _);
            var exit = hasExit ? (int)row["exit"].AsNumber() : 0;
            var passed = row["passed"].AsBool() == true && hasExit && exit == 0 && !ctx.IsCancellationRequested;
            row["passed"] = passed;
            var decision = classify(row);
            var attempt = new JsonObject
            {
                ["case"] = e.Name,
                ["number"] = number,
                ["status"] = "failed",
                ["classification"] = decision.Classification,
                ["retry_of"] = number > 1 ? number - 1 : null,
                ["started_at"] = started.ToString("O", CultureInfo.InvariantCulture),
                ["finished_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["exit"] = hasExit ? exit : null,
                ["error"] = null,
            };
            if (passed)
            {
                attempt["status"] = "passed";
                attempt["classification"] = "none";
            }

            if (ctx.IsCancellationRequested)
            {
                attempt["status"] = "cancelled";
                attempt["classification"] = "cancelled";
                row["error"] = CancelledText(ctx);
            }

            if (row["error"] is JsonValue errorValue && errorValue.TryGetValue<string>(out var message))
            {
                attempt["error"] = message;
            }

            var output = row["output"].AsString();
            var log = row["log"].AsString();
            attempt["evidence"] = EvidenceReference(opts.Output, Path.Combine(output, "result.json"));
            attempt["log"] = EvidenceReference(opts.Output, log);
            var haveEvidence = attempt["evidence"] is not null && attempt["log"] is not null;
            attempts.Add(attempt);
            total += (long)row["wall_ms"].AsNumber();
            boot += (long)row["boot_ms"].AsNumber();
            if (passed || number == 2 || opts.Resume || !decision.Eligible || decision.Classification != "infrastructure"
                || ctx.IsCancellationRequested || !haveEvidence)
            {
                break;
            }

            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ctx);
            stopCts.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await cleanup(root, opts.GameId, stopCts.Token).ConfigureAwait(false);
            }
            catch (AcceptanceException ex)
            {
                row["retry_cleanup_error"] = ex.Message;
                break;
            }
            catch (OperationCanceledException ex)
            {
                row["retry_cleanup_error"] = ex.Message;
                break;
            }

            await stderr.WriteLineAsync($"[worker {worker}] {e.Name} retrying fresh after classified infrastructure failure (attempt 2 of 2)").ConfigureAwait(false);
        }

        row["retry_policy"] = RetryPolicy;
        row["attempts"] = attempts;
        row["attempt_count"] = attempts.Count;
        row["final_attempt"] = attempts.Count > 0 ? attempts.Count : null;
        row["wall_ms"] = total;
        row["boot_ms"] = boot;
        var disposition = row["passed"].AsBool() == true ? "passed" : "failed";
        if (attempts.Count > 1)
        {
            disposition = "retried_" + disposition;
        }

        if (ctx.IsCancellationRequested)
        {
            disposition = "cancelled";
        }

        row["disposition"] = disposition;
        return row;
    }

    private static string CancelledText(CancellationToken ctx) =>
        ctx.IsCancellationRequested ? "context canceled" : "";

    /// <summary>An immutable reference to a file under the suite output: its slash path relative to <paramref name="root"/> and its SHA-256; null when it is not there.</summary>
    internal static JsonObject? EvidenceReference(string root, string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        var rel = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var digest = SHA256.HashData(stream);
            return new JsonObject
            {
                ["path"] = rel.Replace('\\', '/'),
                ["sha256"] = Convert.ToHexStringLower(digest),
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
