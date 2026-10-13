using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// acceptance profile-capture: the loop for native snapshot performance work. It runs the test/profile_capture op
// (WasteFixture.cs), which calls SnapshotFrames.Capture back to back on the game thread, paused, and prints p50/p90/max
// per family and ObservationWork.Detail span whether or not a capture was slow. The game is the root's kept process
// (healed like a run's when the installed mod is stale), and the save is the newest sustained/colony checkpoint so the
// colony has real rows; the next call reloads it, so an edit, a rebuild and a profile take one command.

/// <summary>The <c>acceptance profile-capture</c> subcommand (Go <c>profileCapture</c>).</summary>
public static class ProfileCaptureCommand
{
    public const string Usage = """

          acceptance profile-capture -root <dir> [-n <count> -equality -save <name> | -from <label|dir|latest-ci> -case <area/case> -output <dir> -game <id> -headless=false -timeout <d> -no-heal -json]
            loads the save (default: the -case ring's newest bundle, sustained/colony) on the kept game, runs SnapshotFrames.Capture
            -n times paused and prints p50/p90/max ms and rows per family and detail span; -equality adds the ColonyFacts equality probe
        """;

    /// <summary>The native fixture op the command calls.</summary>
    public const string ProfileOp = "test/profile_capture";

    /// <summary>The profile save the bundle is copied to, overwritten on every call so the loaded colony is the bundle's.</summary>
    public const string ProfileSave = "RimGovernor-profile-capture";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> args, IProfileFixtureHost host, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ProfileOptions p;
        try
        {
            p = ProfileOptions.Parse(args);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        // With -json stdout carries only the stats object.
        var info = p.Json ? stderr : stdout;
        var save = p.Save;
        var ignoreMods = false;
        if (save.Length == 0)
        {
            string bundle;
            try
            {
                bundle = await ProfileBundleAsync(p.Root, p.Case, p.From, cancellationToken).ConfigureAwait(false);
                StageProfileSave(p.Root, p.Case, bundle);
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException or JsonException)
            {
                await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
                return 1;
            }

            var age = "?";
            var savePath = Path.Combine(bundle, Checkpoints.CheckpointSaveName(p.Case) + ".rws");
            if (File.Exists(savePath))
            {
                var minutes = Math.Round((DateTime.UtcNow - File.GetLastWriteTimeUtc(savePath)).TotalMinutes, MidpointRounding.AwayFromZero);
                age = GoDuration.Format(TimeSpan.FromMinutes(minutes));
            }

            await info.WriteLineAsync($"profile-capture: {p.Case} from {bundle} (saved {age} ago)").ConfigureAwait(false);
            save = ProfileSave;

            // A CI bundle may record mods since retired from the build.
            ignoreMods = true;
        }

        var call = new ProfileFixtureCall
        {
            Root = p.Root,
            Output = p.Output,
            GameId = p.GameId,
            Headless = p.Headless,
            Timeout = p.Timeout,
            Save = save,
            IgnoreMods = ignoreMods,
            NoHeal = p.NoHeal,
            Op = ProfileOp,
            Args = new JsonObject { ["count"] = p.Count, ["equality"] = p.Equality },
        };
        if (!await host.PreflightAsync(call, info, cancellationToken).ConfigureAwait(false))
        {
            return 2;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(p.Timeout);
        var started = Stopwatch.StartNew();
        var result = new ProfileFixtureResult
        {
            Op = ProfileOp,
            Args = (JsonObject)call.Args.DeepClone(),
            Output = Path.Combine(p.Output, "profile_capture-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)),
        };
        try
        {
            await host.ExecuteAsync(call, result, timeout.Token).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            result.Error = e.Message;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            result.Error = "profile-capture timed out after " + GoDuration.Format(p.Timeout);
        }

        if (result.Error.Length > 0 || !result.Success)
        {
            await host.PrintFailureAsync(info, result, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        ProfileSummary profile;
        try
        {
            profile = ProfileSummary.Summarize(result.Response ?? []);
        }
        catch (AcceptanceException e)
        {
            await info.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }

        if (p.Json)
        {
            await stdout.WriteLineAsync(profile.ToJson().ToJsonString(Indented)).ConfigureAwait(false);
        }
        else
        {
            await profile.PrintAsync(stdout, cancellationToken).ConfigureAwait(false);
            var elapsed = TimeSpan.FromMilliseconds(Math.Round(started.Elapsed.TotalMilliseconds / 100, MidpointRounding.AwayFromZero) * 100);
            await stdout.WriteLineAsync($"profile-capture: {save} in {GoDuration.Format(elapsed)} ({result.Output})").ConfigureAwait(false);
        }

        return profile.Equality is not null && profile.Equality["equal"].AsBool() != true ? 1 : 0;
    }

    /// <summary>
    /// Resolves the checkpoint bundle holding the save: -from as a directory or a label of the case's ring, else the
    /// ring's newest entry, else its failed bundle.
    /// </summary>
    public static async Task<string> ProfileBundleAsync(string root, string caseName, string from, CancellationToken cancellationToken)
    {
        if (from == CiFixture.LatestCi)
        {
            return await CiFixture.CiBundleAsync(root, caseName, cancellationToken).ConfigureAwait(false);
        }

        if (from.Length > 0 && Directory.Exists(from))
        {
            return from;
        }

        var dir = RingDirectory(root, caseName);
        var ring = Checkpoints.ReadRing(dir);
        if (from.Length > 0)
        {
            if (ring is null)
            {
                throw new AcceptanceException($"no checkpoint ring at {dir} to take \"{from}\" from");
            }

            if (from == "failed" && ring.Failed is not null)
            {
                return ring.Failed.Path;
            }

            return ring.Entry(from) is { } entry ? entry.Path : throw new AcceptanceException($"{dir}: no bundle \"{from}\" in the ring");
        }

        if (CiFixture.NewestInRing(ring) is { Length: > 0 } bundle)
        {
            return bundle;
        }

        // A worktree's root is private, so a fresh one has no ring; borrow the newest bundle a peer checkout's same root
        // holds (read only: the save is copied into this profile).
        if (PeerBundle(root, caseName) is { Length: > 0 } peer)
        {
            return peer;
        }

        throw new AcceptanceException($"no {caseName} checkpoint in {dir} or any peer checkout: run `acceptance run {caseName}` once, or pass -save");
    }

    private static string RingDirectory(string root, string caseName) =>
        Path.Combine(root, "checkpoints", caseName.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The most recently written ring bundle of <paramref name="caseName"/> among the main checkout and its
    /// .claude/worktrees, at root's path relative to its own checkout; "" when root is outside a checkout or none has one.
    /// </summary>
    public static string PeerBundle(string root, string caseName)
    {
        var checkout = NativeSourceInputs.FindRepo(root);
        if (checkout is null)
        {
            return "";
        }

        var rel = Path.GetRelativePath(checkout, root);
        if (rel.StartsWith("..", StringComparison.Ordinal))
        {
            return "";
        }

        var main = checkout;
        var marker = Path.Combine(".claude", "worktrees");
        var at = checkout.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at > 0)
        {
            main = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkout[..at]));
        }

        var worktrees = Path.Combine(main, ".claude", "worktrees");
        var peers = new List<string> { main };
        if (Directory.Exists(worktrees))
        {
            peers.AddRange(Directory.EnumerateDirectories(worktrees).Order(StringComparer.Ordinal));
        }

        var best = "";
        var newest = DateTime.MinValue;
        var own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkout));
        foreach (var peer in peers)
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(peer)), own, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Ring? ring;
            try
            {
                ring = Checkpoints.ReadRing(RingDirectory(Path.Combine(peer, rel), caseName));
            }
            catch (Exception e) when (e is AcceptanceException or IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            var bundle = CiFixture.NewestInRing(ring);
            if (bundle.Length == 0)
            {
                continue;
            }

            var save = Path.Combine(bundle, Checkpoints.CheckpointSaveName(caseName) + ".rws");
            if (File.Exists(save) && File.GetLastWriteTimeUtc(save) > newest)
            {
                best = bundle;
                newest = File.GetLastWriteTimeUtc(save);
            }
        }

        return best;
    }

    /// <summary>Copies the bundle's save into the profile as <see cref="ProfileSave"/>, replacing the last call's copy.</summary>
    public static void StageProfileSave(string root, string caseName, string bundle)
    {
        var source = Path.Combine(bundle, Checkpoints.CheckpointSaveName(caseName) + ".rws");
        var target = Path.Combine(root, "profile", "Saves");
        Directory.CreateDirectory(target);
        File.Copy(source, Path.Combine(target, ProfileSave + ".rws"), overwrite: true);
    }

    private sealed record ProfileOptions
    {
        public string Root { get; init; } = "";

        public int Count { get; init; }

        public bool Equality { get; init; }

        public string Save { get; init; } = "";

        public string Case { get; init; } = "";

        public string From { get; init; } = "";

        public string Output { get; init; } = "";

        public string GameId { get; init; } = "";

        public bool Headless { get; init; }

        public TimeSpan Timeout { get; init; }

        public bool NoHeal { get; init; }

        public bool Json { get; init; }

        public static ProfileOptions Parse(IReadOnlyList<string> args)
        {
            var fs = new FlagSet("profile-capture");
            var root = fs.String("root", "");
            var count = fs.Int("n", 20);
            var equality = fs.Bool("equality", false);
            var save = fs.String("save", "");
            var caseName = fs.String("case", "sustained/colony");
            var from = fs.String("from", "");
            var output = fs.String("output", "");
            var game = fs.String("game", "rimgovernor-trial");
            var headless = fs.Bool("headless", true);
            var timeout = fs.Duration("timeout", TimeSpan.FromMinutes(5));
            var noHeal = fs.Bool("no-heal", false);
            var json = fs.Bool("json", false);
            fs.Parse(args);
            if (fs.Args.Count > 0)
            {
                throw new UsageException("unexpected arguments: [" + string.Join(' ', fs.Args) + "]");
            }

            if (root.Value.Length == 0)
            {
                throw new UsageException("-root is required");
            }

            if (!Path.IsPathFullyQualified(root.Value))
            {
                throw new UsageException($"-root must be absolute: {root.Value}");
            }

            if (count.Value is < 1 or > 1000)
            {
                throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"-n must be 1..1000: {count.Value}"));
            }

            if (save.Value.Length > 0 && from.Value.Length > 0)
            {
                throw new UsageException("-save and -from are exclusive");
            }

            var outputDir = output.Value.Length == 0 ? Path.Combine(root.Value, "acceptance", "fixture") : output.Value;
            return new ProfileOptions
            {
                Root = root.Value,
                Count = count.Value,
                Equality = equality.Value,
                Save = save.Value,
                Case = caseName.Value,
                From = from.Value,
                Output = Path.GetFullPath(outputDir),
                GameId = game.Value,
                Headless = headless.Value,
                Timeout = timeout.Value,
                NoHeal = noHeal.Value,
                Json = json.Value,
            };
        }
    }
}
