using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The run's first step (Go <c>cmd/acceptance/heal.go</c>): the doctor's checks, only the failing ones printed. A Fail
/// refuses the run before any game opens, except a stale or fixture-less install (<see cref="DoctorCheck.Code"/>), which
/// the run heals unless <see cref="RunOptions.NoHeal"/>; the checks then run again.
/// </summary>
public sealed class RunPreflight : IRunPreflight
{
    /// <summary>The shared instance the host wires into <see cref="RunDependencies.Preflight"/>.</summary>
    public static RunPreflight Instance { get; } = new();

    /// <inheritdoc />
    public Task<PreflightResult> RunAsync(IReadOnlyList<AcceptanceCase> selected, RunOptions options, TextWriter stdout, CancellationToken cancellationToken) =>
        PreflightPass.RunAsync(selected, options, stdout, cancellationToken);
}

/// <summary>The preflight pass itself (see <see cref="RunPreflight"/>).</summary>
public static class PreflightPass
{
    /// <summary>The healed-list entry for a root whose kept game the heal stopped.</summary>
    public const string Relaunched = "relaunched";

    /// <summary>
    /// Runs the doctor against <paramref name="selected"/>'s names and fixture ops and heals what it may. The Go version
    /// took the Rimgovernor binary path as a check input; the brain is in-process in the mod now, so the doctor's
    /// run-binary check needs no input here.
    /// </summary>
    public static async Task<PreflightResult> RunAsync(
        IReadOnlyList<AcceptanceCase> selected,
        RunOptions opts,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(stdout);
        var names = new List<string>();
        var ops = new List<string>();
        foreach (var c in selected)
        {
            if (c.Name.Length > 0)
            {
                names.Add(c.Name);
            }

            ops.AddRange(c.FixtureOps());
        }

        var setup = AcceptanceDoctor.Setup ?? new FileDoctorSetup();
        var o = new DoctorOptions { Root = opts.Root, Output = opts.Output, Cases = names, GameId = opts.GameId, FixtureOps = ops };
        var checks = await DoctorChecks.RunAsync(o, setup, cancellationToken).ConfigureAwait(false);
        await DoctorChecks.WriteAsync(stdout, checks, onlyFailing: true).ConfigureAwait(false);
        var healed = new List<string>();
        if (!opts.NoHeal && await DoctorHeal.StopOrphansAsync(checks, stdout, cancellationToken).ConfigureAwait(false) > 0)
        {
            healed.Add(DoctorHealCodes.Orphans);
        }

        if (!DoctorChecks.Failed(checks))
        {
            return new PreflightResult(true, healed);
        }

        var codes = DoctorHeal.Healable(checks);
        if (codes.Count == 0 || opts.NoHeal)
        {
            await stdout.WriteLineAsync(codes.Count > 0
                ? "preflight failed; -no-heal refuses the rebuild a run would otherwise do (acceptance setup fixes it)"
                : "preflight failed; fix the checks above or pass -no-doctor to run anyway").ConfigureAwait(false);
            return new PreflightResult(false, []);
        }

        try
        {
            healed.AddRange(await HealAsync(setup, codes, ops, opts, stdout, cancellationToken).ConfigureAwait(false));
        }
        catch (AcceptanceException e)
        {
            await stdout.WriteLineAsync($"heal failed: {e.Message}\npreflight failed; fix the checks above (acceptance setup) or pass -no-doctor to run anyway").ConfigureAwait(false);
            return new PreflightResult(false, []);
        }

        checks = await DoctorChecks.RunAsync(o, setup, cancellationToken).ConfigureAwait(false);
        await DoctorChecks.WriteAsync(stdout, checks, onlyFailing: true).ConfigureAwait(false);
        if (DoctorChecks.Failed(checks))
        {
            await stdout.WriteLineAsync("preflight still fails after the heal; fix the checks above or pass -no-doctor to run anyway").ConfigureAwait(false);
            return new PreflightResult(false, healed);
        }

        return new PreflightResult(true, healed);
    }

    /// <summary>
    /// Rebuilds and reinstalls the mod a preflight found stale or short of a fixture, the way the by-hand recipe went:
    /// the fixture set is the installed build's plus the classes registering the run's cases' fixture ops; the worktree's
    /// private game copy is stopped first by pid (never by image name); <see cref="SetupEnvironment.RunAsync"/> then
    /// builds and installs into that copy. It only heals a root that is the worktree's own layout
    /// (<see cref="SetupLayout.For"/>): any other root is refused, as the install would land in a copy the run does not
    /// launch. The next game open launches fresh. Returns the codes plus <see cref="Relaunched"/> when a game was
    /// stopped, for the report's healed list.
    /// </summary>
    public static async Task<IReadOnlyList<string>> HealAsync(
        IDoctorSetup setup,
        IReadOnlyList<string> codes,
        IReadOnlyList<string> ops,
        RunOptions opts,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(log);
        var cwd = Directory.GetCurrentDirectory();
        var repo = SetupPackage.FindRepo(cwd)
            ?? throw new AcceptanceException($"no checkout encloses {cwd}; run from the worktree");
        var layout = SetupLayout.For(repo);
        var gameCopy = RootGameCopy(setup, opts);
        if (!string.Equals(Path.GetFullPath(gameCopy).TrimEnd('\\', '/'), Path.GetFullPath(layout.GameCopy).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new AcceptanceException(
                $"{opts.Root} launches {gameCopy}, not this worktree's private copy {layout.GameCopy}; only the worktree's own layout is healed");
        }

        var installed = Path.Combine(gameCopy, "Mods", "RimGovernor");
        IReadOnlyList<string> installedFixtures = [];
        if (setup.ReadPackageManifest(installed) is { } manifest)
        {
            installedFixtures = manifest.Fixtures;
        }

        var fixtures = setup.FixtureFlags(repo, installedFixtures, ops);

        // An install with nothing recorded takes every class.
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"heal\t{string.Join(',', codes)}\trebuilding the mod (fixtures [{string.Join(' ', fixtures)}])")).ConfigureAwait(false);
        var healed = new List<string>(codes);
        var running = await SetupProcesses.RunningGamesAsync(gameCopy, cancellationToken).ConfigureAwait(false);
        if (running.Count > 0)
        {
            // The recorded process tree is the root's game; whatever else runs from the copy (a stale runtime) is still
            // this worktree's game by its executable path, and goes by pid.
            using var stopTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                _ = await Game.StopGameAsync(opts.Root, opts.GameId, stopTimeout.Token).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                await log.WriteLineAsync($"heal\tstop\t{e.Message} (falling back to the copy's pids)").ConfigureAwait(false);
            }

            try
            {
                _ = await SetupProcesses.StopGamesAsync(gameCopy, stopTimeout.Token).ConfigureAwait(false);
            }
            catch (SetupException e)
            {
                throw new AcceptanceException($"stop the root's game: {e.Message}", e);
            }

            await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"heal\tstopped\tpids [{string.Join(' ', running)}] from {gameCopy}; the run launches fresh")).ConfigureAwait(false);
            healed.Add(Relaunched);
        }

        SetupInputs inputs;
        SetupSummary summary;
        try
        {
            inputs = SetupDiscovery.Discover(new SetupOverrides { Repo = repo });
            summary = await SetupEnvironment.RunAsync(
                new SetupOptions { Layout = layout, Inputs = inputs, Fixtures = fixtures, Log = log },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SetupException e)
        {
            throw new AcceptanceException(e.Message, e);
        }

        if (!summary.ModBuilt)
        {
            throw new AcceptanceException($"setup left the installed build alone ({summary.ModReason}) though the preflight found it wanting");
        }

        await log.WriteLineAsync($"heal\tinstalled\t{summary.ModBuild} ({summary.ModReason})").ConfigureAwait(false);
        return healed;
    }

    /// <summary>The game copy the root's configuration launches.</summary>
    public static string RootGameCopy(IDoctorSetup setup, RunOptions opts)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(opts);
        var game = setup.ReadGameSection(opts.Root, opts.GameId);
        if (game.WorkingDir.Length == 0)
        {
            throw new AcceptanceException($"{Path.Combine(opts.Root, "config", "config.json")}: the game has no workingDir");
        }

        return game.WorkingDir;
    }
}
