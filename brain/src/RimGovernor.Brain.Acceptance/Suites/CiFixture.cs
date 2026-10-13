using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Runs the GitHub CLI with the arguments and returns its stdout; a test replaces it (Go <c>ghRunner</c>).</summary>
public delegate Task<string> GhRunner(IReadOnlyList<string> args, CancellationToken cancellationToken);

/// <summary>The newest completed fixture-factory run on main (Go <c>factoryRun</c>).</summary>
public sealed record FactoryRun(long Id, string HeadSha, string Conclusion);

/// <summary>
/// The save-fixture factory (Go <c>cmd/acceptance/cifixture.go</c>): .github/workflows/fixture-factory.yml runs
/// sustained/colony weekly on a Windows runner and uploads its checkpoint ring as the run artifact
/// <c>colony-checkpoints-&lt;commit&gt;</c>. <c>-from latest-ci</c> (profile-capture) and <c>acceptance fetch-fixture</c>
/// download the newest completed run's ring with <c>gh run download</c> into <c>&lt;root&gt;/ci-fixtures/&lt;run id&gt;</c>,
/// so a fresh clone with no local run has a real colony to load.
/// </summary>
public static class CiFixture
{
    /// <summary>The <c>-from</c> label that resolves to the factory's newest ring.</summary>
    public const string LatestCi = "latest-ci";

    /// <summary>The usage lines for the command's help text.</summary>
    public const string FetchFixtureUsage = """
          acceptance fetch-fixture -root <dir> [-case <area/case>]
            downloads the newest completed fixture-factory run's checkpoint ring (once per run) and prints its newest bundle directory
        """;

    private const string FactoryWorkflow = "fixture-factory.yml";
    private const string FactoryArtifact = "colony-checkpoints-";
    private const string FactoryRepo = "davidarcher/rimgovernor";

    /// <summary>How <c>gh</c> is run; replaced by a test.</summary>
    public static GhRunner Gh { get; set; } = RunGhAsync;

    private static async Task<string> RunGhAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("gh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        try
        {
            using var process = Process.Start(info) ?? throw new AcceptanceException("gh: the process did not start");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            return process.ExitCode == 0
                ? text
                : throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"exit status {process.ExitCode}"));
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new AcceptanceException($"gh: {e.Message}", e);
        }
    }

    /// <summary>The newest completed factory run on main; a failed case still publishes its bundle (the run stays red), so success is not required.</summary>
    public static async Task<FactoryRun> NewestFactoryRunAsync(CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await Gh(
                ["run", "list", "-R", FactoryRepo, "--workflow", FactoryWorkflow, "--branch", "main", "--status", "completed", "--limit", "5", "--json", "databaseId,headSha,conclusion"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"gh run list {FactoryWorkflow}: {e.Message}", e);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"gh run list {FactoryWorkflow}: {e.Message}", e);
        }

        foreach (var node in parsed.AsSlice())
        {
            var row = node.AsMap();
            if (row is null)
            {
                continue;
            }

            var id = (long)row["databaseId"].AsNumber();
            var conclusion = row["conclusion"].AsString();
            if (id != 0 && conclusion is "success" or "failure")
            {
                return new FactoryRun(id, row["headSha"].AsString(), conclusion);
            }
        }

        throw new AcceptanceException($"no completed {FactoryWorkflow} run on main: dispatch it once");
    }

    /// <summary>Downloads (once per run id) the newest factory run's checkpoint ring for <paramref name="caseName"/> under <paramref name="root"/> and returns its ring directory.</summary>
    public static async Task<(string Ring, FactoryRun Run)> FetchCiRingAsync(string root, string caseName, CancellationToken cancellationToken)
    {
        var run = await NewestFactoryRunAsync(cancellationToken).ConfigureAwait(false);
        var id = run.Id.ToString(CultureInfo.InvariantCulture);
        var dir = Path.Combine(root, "ci-fixtures", id);
        var ring = FindCiRing(dir, caseName);
        if (ring.Length > 0)
        {
            return (ring, run);
        }

        var partial = dir + ".partial";
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        try
        {
            _ = await Gh(["run", "download", id, "-R", FactoryRepo, "--pattern", FactoryArtifact + "*", "--dir", partial], cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"gh run download {id}: {e.Message}", e);
        }

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.Move(partial, dir);
        ring = FindCiRing(dir, caseName);
        if (ring.Length == 0)
        {
            throw new AcceptanceException($"run {id}'s artifact holds no {caseName} checkpoint ring");
        }

        return (ring, run);
    }

    /// <summary>The ring directory for <paramref name="caseName"/> in a downloaded factory artifact (&lt;dir&gt;/&lt;artifact&gt;/checkpoints/&lt;case&gt;), empty when none holds an entry.</summary>
    public static string FindCiRing(string dir, string caseName)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(caseName);
        if (!Directory.Exists(dir))
        {
            return "";
        }

        foreach (var artifact in Directory.EnumerateDirectories(dir, FactoryArtifact + "*").Order(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(artifact, "checkpoints", caseName.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate) && NewestInRing(ReadRingOrNull(candidate)).Length > 0)
            {
                return candidate;
            }
        }

        return "";
    }

    private static Ring? ReadRingOrNull(string dir)
    {
        try
        {
            return Checkpoints.ReadRing(dir);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The ring's newest bundle directory: its last entry, else its failed bundle, else empty (Go <c>newestInRing</c>).</summary>
    public static string NewestInRing(Ring? ring)
    {
        if (ring is null)
        {
            return "";
        }

        if (ring.Entries.Count > 0)
        {
            return ring.Entries[^1].Path;
        }

        return ring.Failed?.Path ?? "";
    }

    /// <summary>The newest bundle of the factory's ring for <paramref name="caseName"/> (Go <c>ciBundle</c>).</summary>
    public static async Task<string> CiBundleAsync(string root, string caseName, CancellationToken cancellationToken)
    {
        var (dir, _) = await FetchCiRingAsync(root, caseName, cancellationToken).ConfigureAwait(false);
        return NewestInRing(Checkpoints.ReadRing(dir));
    }

    /// <summary>The colony snapshot streams the factory recorded for <paramref name="caseName"/> beside its ring (&lt;artifact&gt;/snapshots/&lt;case&gt;/routine-stream-*.jsonl).</summary>
    public static IReadOnlyList<string> CiStreams(string ring, string caseName)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(caseName);
        var artifact = ring;
        for (var i = 0; i < caseName.Split('/').Length; i++)
        {
            artifact = Path.GetDirectoryName(artifact) ?? artifact;
        }

        artifact = Path.GetDirectoryName(artifact) ?? artifact;
        var dir = Path.Combine(artifact, "snapshots", caseName.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "routine-stream-*.jsonl").Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary><c>acceptance fetch-fixture</c>: prints the newest bundle directory of the newest completed factory run's ring; returns the exit code (2 usage, 1 a failed fetch).</summary>
    public static async Task<int> FetchFixtureAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var fs = new FlagSet("fetch-fixture");
        var root = fs.String("root", "");
        var caseName = fs.String("case", "sustained/colony");
        try
        {
            fs.Parse(args);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        if (root.Value.Length == 0 || !Path.IsPathFullyQualified(root.Value) || fs.Args.Count > 0)
        {
            await stderr.WriteLineAsync("-root <absolute dir> is required").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var (dir, run) = await FetchCiRingAsync(root.Value, caseName.Value, cancellationToken).ConfigureAwait(false);
            var ring = Checkpoints.ReadRing(dir);
            await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"fetch-fixture: run {run.Id} at {run.HeadSha.Trim()}, ring {dir}")).ConfigureAwait(false);
            var streams = CiStreams(dir, caseName.Value);
            foreach (var s in streams)
            {
                await stderr.WriteLineAsync($"fetch-fixture: stream {s}").ConfigureAwait(false);
            }

            if (streams.Count > 0)
            {
                await stderr.WriteLineAsync("fetch-fixture: load a review from the stream with the snapshot tooling at the tick the case failed").ConfigureAwait(false);
            }

            await stdout.WriteLineAsync(NewestInRing(ring)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
