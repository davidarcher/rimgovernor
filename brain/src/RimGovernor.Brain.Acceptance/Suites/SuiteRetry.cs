using System.Security.Cryptography;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One classification of a failed attempt and whether a retry may follow it.</summary>
internal readonly record struct RetryDecision(string Classification, bool Eligible);

/// <summary>
/// The suite's retry wrapper: it keeps the original row shape as the final projection
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
    internal delegate Task<SuiteRow> AttemptRunner(SuiteEntry entry, SuiteOptions opts, string root, int worker, TextWriter stderr, CancellationToken cancellationToken);

    internal delegate RetryDecision RetryClassifier(SuiteRow row);

    internal delegate Task RetryCleanup(string root, string gameId, CancellationToken cancellationToken);

    internal static Task<SuiteRow> RunEntryAsync(SuiteEntry e, SuiteOptions opts, string root, int worker, TextWriter stderr, CancellationToken cancellationToken) =>
        RunEntryAttemptsAsync(e, opts, root, worker, stderr, SuiteRunner.RunEntryOnceAsync, ClassifyAttempt, StopRetryGameAsync, cancellationToken);

    internal static RetryDecision ClassifyAttempt(SuiteRow row) =>
        row.Passed ? new RetryDecision("none", false) : new RetryDecision("unknown", false);

    /// <summary>
    /// Stops the root's game and requires a positive confirmation that it is gone, not merely a successful stop request:
    /// neither profile's launch record may still name a live process.
    /// </summary>
    internal static async Task StopRetryGameAsync(string root, string gameId, CancellationToken cancellationToken)
    {
        _ = await Game.StopGameAsync(root, gameId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var profile in new[] { "config-headless", "config-rendered" })
        {
            var dir = Path.Combine(root, profile);
            if (Directory.Exists(dir) && HeadlessProfile.GameRunning(dir, gameId))
            {
                throw new AcceptanceException($"retry cleanup did not confirm stopped game: still running under {profile}");
            }
        }
    }

    internal static async Task<SuiteRow> RunEntryAttemptsAsync(
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
        var attempts = new List<SuiteAttempt>();
        var row = new SuiteRow { Name = e.Name };
        long total = 0;
        long boot = 0;
        for (var number = 1; number <= 2; number++)
        {
            if (ctx.IsCancellationRequested)
            {
                row.Passed = false;
                row.Error = CancelledText(ctx);
                break;
            }

            var current = opts;
            if (number == 2)
            {
                current = opts with { Output = Path.Combine(opts.Output, "attempts", "2") };
            }

            var started = DateTimeOffset.UtcNow;
            row = await run(e, current, root, worker, stderr, ctx).ConfigureAwait(false);
            var exit = row.Exit;
            var passed = row.Passed && exit == 0 && !ctx.IsCancellationRequested;
            row.Passed = passed;
            var decision = classify(row);
            var status = "failed";
            var classification = decision.Classification;
            if (passed)
            {
                status = "passed";
                classification = "none";
            }

            if (ctx.IsCancellationRequested)
            {
                status = "cancelled";
                classification = "cancelled";
                row.Error = CancelledText(ctx);
            }

            var evidence = EvidenceReferenceOf(opts.Output, Path.Combine(row.Output ?? "", "result.json"));
            var log = EvidenceReferenceOf(opts.Output, row.Log ?? "");
            attempts.Add(new SuiteAttempt
            {
                Case = e.Name,
                Number = number,
                Status = status,
                Classification = classification,
                RetryOf = number > 1 ? number - 1 : null,
                StartedAt = started,
                FinishedAt = DateTimeOffset.UtcNow,
                Exit = exit,
                Error = row.Error,
                Evidence = evidence,
                Log = log,
            });
            total += row.WallMs ?? 0;
            boot += row.BootMs ?? 0;
            if (passed || number == 2 || !decision.Eligible || decision.Classification != "infrastructure"
                || ctx.IsCancellationRequested || evidence is null || log is null)
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
                row.RetryCleanupError = ex.Message;
                break;
            }
            catch (OperationCanceledException ex)
            {
                row.RetryCleanupError = ex.Message;
                break;
            }

            await stderr.WriteLineAsync($"[worker {worker}] {e.Name} retrying fresh after classified infrastructure failure (attempt 2 of 2)").ConfigureAwait(false);
        }

        row.RetryPolicy = RetryPolicy;
        row.Attempts = attempts;
        row.WallMs = total;
        row.BootMs = boot;
        var disposition = row.Passed ? "passed" : "failed";
        if (attempts.Count > 1)
        {
            disposition = "retried_" + disposition;
        }

        if (ctx.IsCancellationRequested)
        {
            disposition = "cancelled";
        }

        row.Disposition = disposition;
        return row;
    }

    private static string CancelledText(CancellationToken ctx) =>
        ctx.IsCancellationRequested ? "context canceled" : "";

    /// <summary>An immutable reference to a file under the suite output: its slash path relative to <paramref name="root"/> and its SHA-256; null when it is not there.</summary>
    internal static EvidenceReference? EvidenceReferenceOf(string root, string path)
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
            return new EvidenceReference(rel.Replace('\\', '/'), Convert.ToHexStringLower(digest));
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
