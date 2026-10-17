using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>What <see cref="Bootstrapper.RunAsync"/> needs.</summary>
public sealed record BootstrapOptions
{
    /// <summary>The tested checkout.</summary>
    public required string Repo { get; init; }

    /// <summary>The new, short, absolute plaintext job directory.</summary>
    public required string Work { get; init; }

    /// <summary>The ciphertext cache parent directory.</summary>
    public required string Cache { get; init; }

    /// <summary>The bundle manifest path.</summary>
    public required string Manifest { get; init; }

    /// <summary>The exact digest the manifest must have.</summary>
    public required string ManifestSha256 { get; init; }

    /// <summary>The age identity file supplied by the trusted runner.</summary>
    public required string Identity { get; init; }

    /// <summary>The trusted workflow's authorization.</summary>
    public required Trust Trust { get; init; }

    /// <summary>The pinned 7-Zip and age executables.</summary>
    public required Tools Tools { get; init; }

    /// <summary>The mod role; only <c>fixture</c> exists since #751.</summary>
    public string Role { get; init; } = "fixture";

    /// <summary>Where progress and the setup output go; null discards.</summary>
    public TextWriter? Log { get; init; }
}

/// <summary>
/// Trust checks and the bootstrap sequence. Bootstrap is called only after workflow trust
/// gating and before any game is launched; even cache hits authenticate the origin and verify all ciphertext.
/// </summary>
public static partial class Bootstrapper
{
    /// <summary>Checks the authorization against the repository, tested commit, event and workflow revision.</summary>
    public static void ValidateTrust(Trust trust, string repository, string head, string eventName, string workflow)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var bad = !BundleFiles.IsRepository(trust.Repository) || !Sha1Hex().IsMatch(trust.TestedCommit) || !Sha1Hex().IsMatch(trust.WorkflowCommit)
            || trust.Repository != repository || trust.TestedCommit != head || trust.WorkflowCommit != workflow || trust.Event != eventName
            || (eventName != "workflow_dispatch" && eventName != "schedule");
        BundleException.Require(!bad, "bootstrap trust mismatch: require same repository, reviewed exact commit, trusted workflow commit and push/dispatch event");
    }

    /// <summary>Checks the trust against the environment and the checkout: its HEAD, a protected main for scheduled runs, and a clean tree.</summary>
    public static async Task CheckTrustAsync(string repo, Trust trust, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var head = await Git(repo, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        ValidateTrust(trust, Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "", head.Trim(), Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") ?? "", Environment.GetEnvironmentVariable("GITHUB_WORKFLOW_SHA") ?? "");
        if (trust.Event == "schedule" && (Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/main" || Environment.GetEnvironmentVariable("GITHUB_REF_PROTECTED") != "true"))
        {
            throw new BundleException("push bootstrap requires protected main");
        }

        var status = await Git(repo, ["status", "--porcelain", "--untracked-files=no"], cancellationToken).ConfigureAwait(false);
        BundleException.Require(status.Trim().Length == 0, "bootstrap requires a clean tested checkout");
    }

    /// <summary>
    /// Prevents a tested checkout from supplying its own authorization. The trusted workflow still owns the file's
    /// creation and content.
    /// </summary>
    public static void CheckAuthorizationPath(string repo, string authorization)
    {
        var resolvedRepo = BundleFiles.ResolveSource(repo);
        var resolvedAuthorization = BundleFiles.ResolveSource(authorization);
        BundleException.Require(!BundleFiles.Within(resolvedRepo, resolvedAuthorization), "authorization must be supplied outside the tested checkout by the trusted workflow");
    }

    /// <summary>
    /// Runs the whole bootstrap and writes <c>bootstrap.json</c> into the job directory, pass or fail. The returned
    /// report is the same record; a failure throws after the report is written.
    /// </summary>
    public static async Task<BootstrapReport> RunAsync(BootstrapOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = Stopwatch.StartNew();
        await CheckTrustAsync(options.Repo, options.Trust, cancellationToken).ConfigureAwait(false); // before cache access
        BundleException.Require(OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64, "bootstrap requires Windows x64");
        BundleException.Require(options.Role == "fixture", "role must be fixture");
        BundleException.Require(
            Path.IsPathRooted(options.Work) && Path.Combine(options.Work, "layout", "native-rimworld").Length <= 140,
            "use a short absolute writable job directory");
        BundleException.Require(!BundleFiles.Within(options.Work, options.Cache) && !BundleFiles.Within(options.Cache, options.Work), "ciphertext cache and plaintext work must be disjoint");
        try
        {
            if (BundleFiles.Exists(options.Work))
            {
                throw new IOException("directory exists");
            }

            Directory.CreateDirectory(options.Work);
        }
        catch (IOException e)
        {
            throw new BundleException($"job work directory must be new: {e.Message}", e);
        }

        var report = new BootstrapReport(
            1, options.ManifestSha256, BundleOrigin.CacheKey(options.ManifestSha256), false, "windows", "x64", Environment.ProcessorCount,
            Environment.GetEnvironmentVariable("ImageVersion") ?? "", options.Role, "", 0, 0, 0, 0, "", "", false, "", 0);
        try
        {
            report = await StepsAsync(options, report, cancellationToken).ConfigureAwait(false);
            report = report with { Passed = true };
            return report;
        }
        catch (Exception e) when (e is BundleException or IOException or UnauthorizedAccessException)
        {
            report = report with { Passed = false, Error = e.Message };
            throw;
        }
        finally
        {
            report = report with { TotalMs = started.ElapsedMilliseconds };
            BundleJson.WriteReport(Path.Combine(options.Work, "bootstrap.json"), report);
        }
    }

    private static async Task<BootstrapReport> StepsAsync(BootstrapOptions o, BootstrapReport report, CancellationToken cancellationToken)
    {
        var manifest = ManifestRules.ReadManifest(o.Manifest, o.ManifestSha256);
        BundleException.Require(manifest.Origin.Repository == o.Trust.Repository, "bundle origin must be the trusted public source repository");
        var inventory = ManifestRules.ReadInventory(Path.GetDirectoryName(Path.GetFullPath(o.Manifest)) ?? ".", manifest);
        var free = BundleFiles.FreeDisk(o.Work);
        report = report with { FreeDiskBytes = free };
        var required = (ulong)manifest.UnpackedBytes * 2 + (2UL << 30);
        foreach (var part in manifest.Parts)
        {
            required += (ulong)part.Bytes * 2;
        }

        BundleException.Require(free >= required, $"insufficient disk: need {required} bytes for verified extraction/build, have {free}");
        var timer = Stopwatch.StartNew();
        var hit = await BundleOrigin.RestoreAsync(o.Cache, o.ManifestSha256, manifest, cancellationToken).ConfigureAwait(false);
        report = report with { CacheHit = hit, RestoreMs = timer.ElapsedMilliseconds };
        var tree = Path.Combine(o.Work, "dependencies");
        timer.Restart();
        await BundleArchive.ExtractAsync(o.Tools, Path.Combine(o.Cache, report.CacheKey), tree, o.Identity, manifest, inventory, cancellationToken).ConfigureAwait(false);
        report = report with { ExtractMs = timer.ElapsedMilliseconds };
        var starts = BundlePack.ValidateStarts(tree, o.Repo, manifest);
        report = report with { Starts = starts };
        var log = o.Log ?? TextWriter.Null;
        log.WriteLine(starts);

        // Tool installation and pinning is handled by bootstrap_remote.ps1 before this runs; setup remains the only
        // native-build entry point.
        var layout = Path.Combine(o.Work, "layout");
        var root = Path.Combine(layout, "bridge");
        var (executable, leading) = SelfInvocation.Resolve();
        report = report with { Root = root, Tool = executable };
        async Task Run(params string[] arguments)
        {
            var code = await ToolProcess.StreamAsync(o.Repo, executable, [.. leading, "acceptance", .. arguments], scrub: true, log, cancellationToken).ConfigureAwait(false);
            BundleException.Require(code == 0, $"exit status {code}");
        }

        timer.Restart();
        try
        {
            await Run(
                "setup", "-worktree", o.Repo, "-layout", layout, "-explicit",
                "-rimworld", Path.Combine(tree, "game"),
                "-harmony-mod", Path.Combine(tree, "harmony"),
                "-harmony", Path.Combine(tree, "harmony", "1.7", "Assemblies", "0Harmony.dll")).ConfigureAwait(false);
        }
        catch (BundleException e)
        {
            throw new BundleException($"explicit acceptance setup: {e.Message}", e);
        }

        report = report with { BuildMs = timer.ElapsedMilliseconds };
        BundlePack.StageStarts(tree, root, starts);
        try
        {
            await Run("doctor", "-root", root, "-worktree", o.Repo).ConfigureAwait(false);
        }
        catch (BundleException e)
        {
            throw new BundleException($"runner preflight: {e.Message}", e);
        }

        return report;
    }

    private static async Task<string> Git(string repo, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.CaptureAsync("", "git", ["-C", repo, .. arguments], scrub: false, cancellationToken).ConfigureAwait(false);
        BundleException.Require(result.ExitCode == 0, $"git {string.Join(' ', arguments)}: exit status {result.ExitCode}: {result.Stderr}");
        return result.Stdout;
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sha1Hex();
}
