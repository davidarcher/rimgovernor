using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>acceptance prune</c> (Go <c>cmd/acceptance/prune.go</c>): deletes old run outputs under one directory. Each child
/// directory holding a result.json within <see cref="PruneDepth"/> is a run or suite output, ordered by modification
/// time, and every one past the newest <c>-keep</c> is removed with the <c>&lt;name&gt;.log</c> and <c>&lt;name&gt;.err</c>
/// a background launch wrote beside it. Anything else under the directory is left alone and named, so a root or a
/// profile passed by mistake loses nothing.
/// </summary>
public static class AcceptancePrune
{
    /// <summary>Usage text for the command's help.</summary>
    public const string Usage = """
          acceptance prune -output <dir> [-keep <n> -dry-run]
            deletes the oldest run outputs under <dir> (the parent of the -output directories runs and suites wrote),
            keeping the newest n (default 5) and their <name>.log/.err; loose files and directories holding no result.json stay
        """;

    /// <summary>How deep a run output nests its result.json: &lt;suite&gt;/&lt;area&gt;/&lt;case&gt;/result.json.</summary>
    private const int PruneDepth = 3;

    /// <summary>Runs the subcommand with the arguments after <c>prune</c>; returns the exit code (2 usage, 1 failure).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        string output = "";
        int keep = 5;
        bool dryRun = false;
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i].TrimStart('-');
            switch (name)
            {
                case "output" when i + 1 < args.Count:
                    output = args[++i];
                    break;
                case "keep" when i + 1 < args.Count:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out keep))
                    {
                        await stderr.WriteLineAsync($"-keep must be a number: {args[i]}").ConfigureAwait(false);
                        return 2;
                    }

                    break;
                case "dry-run" when args[i].StartsWith('-'):
                    dryRun = true;
                    break;
                default:
                    await stderr.WriteLineAsync($"prune: unexpected argument {args[i]}\n{Usage}").ConfigureAwait(false);
                    return 2;
            }
        }

        if (output.Length == 0 || !Path.IsPathFullyQualified(output))
        {
            await stderr.WriteLineAsync("-output must be an absolute directory").ConfigureAwait(false);
            return 2;
        }

        if (keep < 0)
        {
            await stderr.WriteLineAsync("-keep must be 0 or more").ConfigureAwait(false);
            return 2;
        }

        PrunePlan plan;
        try
        {
            plan = PlanPrune(output, keep);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }

        foreach (string skipped in plan.Skipped)
        {
            await stdout.WriteLineAsync($"skip {skipped} (no result.json)").ConfigureAwait(false);
        }

        foreach (PruneRun kept in plan.Kept)
        {
            await stdout.WriteLineAsync($"keep {kept.Name}").ConfigureAwait(false);
        }

        long freed = 0;
        foreach (PruneRun run in plan.Remove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string verb = dryRun ? "would delete" : "delete";
            await stdout.WriteLineAsync($"{verb} {run.Name} ({HumanBytes(run.Bytes)})").ConfigureAwait(false);
            if (!dryRun)
            {
                foreach (string path in run.Paths)
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                        }
                        else
                        {
                            File.Delete(path);
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        await stderr.WriteLineAsync($"delete {path}: {e.Message}").ConfigureAwait(false);
                        return 1;
                    }
                }
            }

            freed += run.Bytes;
        }

        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{plan.Kept.Count} run outputs kept, {plan.Remove.Count} removed, {HumanBytes(freed)} freed")).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Lists the run outputs under <paramref name="dir"/>, newest first, and splits them at <paramref name="keep"/>.</summary>
    internal static PrunePlan PlanPrune(string dir, int keep)
    {
        var skipped = new List<string>();
        var runs = new List<PruneRun>();
        foreach (string path in Directory.EnumerateDirectories(dir))
        {
            string name = Path.GetFileName(path);
            if (!ScanRun(path, out long bytes, out long modified))
            {
                skipped.Add(name);
                continue;
            }

            var paths = new List<string> { path };
            foreach (string suffix in new[] { ".log", ".err" })
            {
                var side = new FileInfo(path + suffix);
                if (side.Exists)
                {
                    paths.Add(side.FullName);
                    bytes += side.Length;
                }
            }

            runs.Add(new PruneRun(name, paths, bytes, modified));
        }

        // Newest first; OrderByDescending is stable, so equal stamps keep directory order.
        IReadOnlyList<PruneRun> ordered = [.. runs.OrderByDescending(r => r.Modified)];
        int kept = Math.Min(keep, ordered.Count);
        return new PrunePlan([.. ordered.Take(kept)], [.. ordered.Skip(kept)], skipped);
    }

    /// <summary>
    /// Sizes a directory and reports whether it is a run output: a result.json lies within <see cref="PruneDepth"/> of it.
    /// <paramref name="modified"/> is the newest file write anywhere under it, so a suite still being written sorts
    /// newest whatever its directory's own stamp says.
    /// </summary>
    private static bool ScanRun(string dir, out long bytes, out long modified)
    {
        bytes = 0;
        modified = 0;
        bool found = false;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            bytes += info.Length;
            long stamp = info.LastWriteTimeUtc.Ticks;
            if (stamp > modified)
            {
                modified = stamp;
            }

            if (info.Name == "result.json")
            {
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (rel.Count(ch => ch == '/') < PruneDepth)
                {
                    found = true;
                }
            }
        }

        return found;
    }

    internal static string HumanBytes(long n)
    {
        const int unit = 1024;
        if (n < unit)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{n} B");
        }

        double value = (double)n / unit;
        int exp = 0;
        for (; value >= unit && exp < 3; exp++)
        {
            value /= unit;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:F1} {"KMGT"[exp]}iB");
    }

    internal sealed record PruneRun(string Name, IReadOnlyList<string> Paths, long Bytes, long Modified);

    internal sealed record PrunePlan(IReadOnlyList<PruneRun> Kept, IReadOnlyList<PruneRun> Remove, IReadOnlyList<string> Skipped);
}
