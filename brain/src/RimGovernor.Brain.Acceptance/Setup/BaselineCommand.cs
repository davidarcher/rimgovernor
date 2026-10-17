namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>acceptance baseline</c>: regenerates the shared tribal-8 baseline save (<see cref="Baselines.BaselineStart"/>) under
/// <c>-root</c> outside the case runner and the case catalog, even when its stamp is current. It drops the stamp and
/// runs the harness's own first-use generation (<see cref="Baselines.EnsureSaveAsync"/>): the scenario start through the
/// new-colony op on a Core-only profile, the save persisted into profile/Saves and stamped.
/// </summary>
public static class BaselineCommand
{
    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance baseline -root <dir> [-game <id> -rendered]
            regenerates the tribal8 baseline save under <root>/profile/Saves from its spec and stamps it, even when the stamp is current
        """;

    /// <summary>Runs the subcommand with the arguments after <c>baseline</c>; returns the exit code (2 refused, 1 a failed generation).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var fs = new FlagSet("baseline");
        var root = fs.String("root", "");
        var game = fs.String("game", "");
        var rendered = fs.Bool("rendered", false);
        try
        {
            fs.Parse(args);
            if (fs.Args.Count > 0)
            {
                throw new UsageException("unexpected arguments: [" + string.Join(' ', fs.Args) + "]");
            }

            if (root.Value.Length == 0 || !Path.IsPathFullyQualified(root.Value))
            {
                throw new UsageException("-root <absolute dir> is required");
            }
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync(e.Message + "\n" + Usage).ConfigureAwait(false);
            return 2;
        }

        var config = new Config
        {
            Root = root.Value,
            Output = Path.Combine(root.Value, "acceptance", "baseline"),
            Headless = !rendered.Value,
            GameId = game.Value,
        };
        try
        {
            File.Delete(Baselines.StartStampPath(root.Value, Baselines.BaselineName));
            await Baselines.EnsureSaveAsync(config, Baselines.BaselineName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync("FAIL: " + e.Message).ConfigureAwait(false);
            return 1;
        }

        await stdout.WriteLineAsync("baseline: " + Baselines.StartSavePath(root.Value, Baselines.BaselineName)).ConfigureAwait(false);
        await stdout.WriteLineAsync("PASS").ConfigureAwait(false);
        return 0;
    }
}
