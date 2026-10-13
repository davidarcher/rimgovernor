namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>acceptance doctor</c> (Go <c>cmd/acceptance/doctor.go</c> and the orphan half of <c>heal.go</c>): the preflight on
/// its own, every check with its fix, exit 1 only when one would certainly fail a run. <c>-heal</c> stops the game
/// processes the orphans check finds running from removed worktrees and reports again.
/// </summary>
public static class AcceptanceDoctor
{
    /// <summary>
    /// The setup layer the checks read the installed package through; the lead assigns the Setup-backed implementation
    /// at startup, otherwise <see cref="FileDoctorSetup"/> is used.
    /// </summary>
    public static IDoctorSetup? Setup { get; set; }

    /// <summary>Usage line for the command's help text.</summary>
    public const string Usage = "  acceptance doctor -root <dir> [-output <dir> -game <id> -worktree <dir> -heal]";

    /// <summary>Runs the subcommand with the arguments after <c>doctor</c>; returns the exit code (2 usage, 1 a certain failure).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        IDoctorSetup setup = Setup ?? new FileDoctorSetup();
        var o = new DoctorOptions { GameId = setup.GameId };
        bool heal = false;
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i].TrimStart('-');
            switch (name)
            {
                case "root" when i + 1 < args.Count:
                    o.Root = args[++i];
                    break;
                case "output" when i + 1 < args.Count:
                    o.Output = args[++i];
                    break;
                case "game" when i + 1 < args.Count:
                    o.GameId = args[++i];
                    break;
                case "worktree" when i + 1 < args.Count:
                    o.Repo = args[++i];
                    break;
                case "heal" when args[i].StartsWith('-'):
                    heal = true;
                    break;
                default:
                    await stderr.WriteLineAsync($"doctor: unexpected argument {args[i]}\n{Usage}").ConfigureAwait(false);
                    return 2;
            }
        }

        if (o.Root.Length == 0)
        {
            await stderr.WriteLineAsync("-root is required").ConfigureAwait(false);
            return 2;
        }

        if (!Path.IsPathFullyQualified(o.Root))
        {
            await stderr.WriteLineAsync($"-root must be absolute: {o.Root}").ConfigureAwait(false);
            return 2;
        }

        IReadOnlyList<DoctorCheck> checks = await DoctorChecks.RunAsync(o, setup, cancellationToken).ConfigureAwait(false);
        await DoctorChecks.WriteAsync(stdout, checks, onlyFailing: false).ConfigureAwait(false);
        if (heal && await DoctorHeal.StopOrphansAsync(checks, stdout, cancellationToken).ConfigureAwait(false) > 0)
        {
            checks = await DoctorChecks.RunAsync(o, setup, cancellationToken).ConfigureAwait(false);
            await DoctorChecks.WriteAsync(stdout, checks, onlyFailing: true).ConfigureAwait(false);
        }

        return DoctorChecks.Failed(checks) ? 1 : 0;
    }
}

/// <summary>
/// The heal helpers a preflight shares with <c>doctor -heal</c> (Go <c>cmd/acceptance/heal.go</c>). The stale-mod
/// rebuild itself (Go <c>heal</c>, which drove <c>setup.Run</c>) belongs to the Setup layer; <see cref="Healable"/> says
/// which failures it may act on.
/// </summary>
public static class DoctorHeal
{
    /// <summary>
    /// Stops the harness processes the orphans check found running from removed worktrees: nobody's game, so a preflight
    /// ends them without asking. Returns how many it stopped.
    /// </summary>
    public static async Task<int> StopOrphansAsync(IReadOnlyList<DoctorCheck> checks, TextWriter log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(log);
        foreach (DoctorCheck c in checks)
        {
            if (c.Code != DoctorHealCodes.Orphans || c.Pids.Count == 0)
            {
                continue;
            }

            int[] pids = [.. c.Pids];
            try
            {
                HarnessProcesses.StopPids(pids, cancellationToken);
            }
            catch (AcceptanceException e)
            {
                await log.WriteLineAsync($"heal\torphans\t{e.Message}").ConfigureAwait(false);
                return 0;
            }

            await log.WriteLineAsync($"heal\torphans\tstopped pids {DoctorText.Join(pids)} from removed worktrees").ConfigureAwait(false);
            return pids.Length;
        }

        return 0;
    }

    /// <summary>
    /// Lists the heal codes of the failing checks, or nothing when any Fail carries no code: a heal fixes the install,
    /// never the rest.
    /// </summary>
    public static IReadOnlyList<string> Healable(IReadOnlyList<DoctorCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var codes = new List<string>();
        foreach (DoctorCheck c in checks)
        {
            if (c.Status != DoctorStatus.Fail)
            {
                continue;
            }

            if (c.Code.Length == 0)
            {
                return [];
            }

            codes.Add(c.Code);
        }

        return codes;
    }
}
