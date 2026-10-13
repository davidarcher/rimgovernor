using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <c>acceptance setup</c> subcommand (Go <c>cmd/acceptance/setup.go</c> runSetup/parseSetup): discovers the inputs,
/// builds the worktree's private environment and prints the summary with the run command a first case needs.
/// </summary>
public sealed class AcceptanceSetup
{
    /// <summary>The setup usage lines, for the command's usage text.</summary>
    public const string Usage = """
          acceptance setup [-worktree <dir>] [-rimworld <RimWorld dir>] [-harmony <0Harmony.dll>]
                           [-fixture A,B] [-rebuild] [-skip-mod]
          acceptance setup generate <variantsave-<save>|variantsave-all> [run flags]

        """;

    private readonly Func<IReadOnlyList<string>, TextWriter, TextWriter, CancellationToken, Task<int>>? _generate;

    /// <summary>
    /// <paramref name="generate"/> runs <c>setup generate &lt;name&gt; [run flags]</c> (it needs the case registry and runner,
    /// which the command host owns); null makes <c>generate</c> report that it is unavailable.
    /// </summary>
    public AcceptanceSetup(Func<IReadOnlyList<string>, TextWriter, TextWriter, CancellationToken, Task<int>>? generate = null)
    {
        _generate = generate;
    }

    /// <summary>Runs the subcommand with the arguments after <c>setup</c>; returns the exit code (2 usage, 1 failure).</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Count > 0 && string.Equals(args[0], "generate", StringComparison.Ordinal))
        {
            if (_generate is null)
            {
                await stderr.WriteLineAsync("setup generate is not available in this build").ConfigureAwait(false);
                return 2;
            }

            return await _generate([.. args.Skip(1)], stdout, stderr, cancellationToken).ConfigureAwait(false);
        }

        Parsed parsed;
        try
        {
            if (!Parse(args, out parsed))
            {
                await stdout.WriteAsync(Usage).ConfigureAwait(false);
                return 0;
            }
        }
        catch (SetupException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var inputs = SetupDiscovery.Discover(parsed.Overrides);
            var layout = SetupLayout.For(parsed.Repo);
            if (parsed.LayoutDir.Length > 0)
            {
                if (!Path.IsPathRooted(parsed.LayoutDir))
                {
                    await stderr.WriteLineAsync("-layout must be absolute").ConfigureAwait(false);
                    return 2;
                }

                layout = SetupLayout.Under(layout.Repo, parsed.LayoutDir);
            }

            var s = await SetupEnvironment.RunAsync(
                new SetupOptions
                {
                    Layout = layout,
                    Inputs = inputs,
                    Fixtures = parsed.Fixtures,
                    Rebuild = parsed.Rebuild,
                    SkipMod = parsed.SkipMod,
                    Log = stdout,
                },
                cancellationToken).ConfigureAwait(false);
            var fixtures = string.Create(CultureInfo.InvariantCulture, $"{s.Fixtures.Count} classes");
            var mod = s.ModReason;
            if (parsed.SkipMod)
            {
                fixtures = "unchanged";
                mod = "left alone (-skip-mod)";
            }

            await stdout.WriteAsync($"\nready\n  root      {s.Layout.Root}\n  game copy {s.Layout.GameCopy}\n  fixtures  {fixtures}\n  mod       {mod}\n").ConfigureAwait(false);
            await stdout.WriteAsync($"\nrun a case:\n  {s.RunCommand}\nstop the kept game:\n  {s.StopCommand}\n").ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private sealed class Parsed
    {
        public string Repo { get; set; } = "";

        public string LayoutDir { get; set; } = "";

        public SetupOverrides Overrides { get; } = new();

        public IReadOnlyList<string>? Fixtures { get; set; }

        public bool Rebuild { get; set; }

        public bool SkipMod { get; set; }
    }

    /// <summary>
    /// Resolves the setup flags: the worktree (the checkout enclosing the working directory unless -worktree names one)
    /// and the discovery overrides. -fixture narrows the mod build to the named classes (every class the build script
    /// accepts by default). Returns false for a help request. <c>-skip-binaries</c> is accepted and ignored: there are
    /// no controller binaries to build any more.
    /// </summary>
    private static bool Parse(IReadOnlyList<string> args, out Parsed result)
    {
        var o = new Parsed();
        result = o;
        var fixtures = "";
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Length < 2 || arg[0] != '-')
            {
                positional.Add(arg);
                continue;
            }

            var name = arg.TrimStart('-');
            string? inline = null;
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                inline = name[(eq + 1)..];
                name = name[..eq];
            }

            string Value()
            {
                if (inline is not null)
                {
                    return inline;
                }

                if (i + 1 >= args.Count)
                {
                    throw new SetupException($"flag needs an argument: -{name}");
                }

                return args[++i];
            }

            bool Flag() => inline is null || bool.Parse(inline);

            switch (name)
            {
                case "h" or "help":
                    return false;
                case "worktree":
                    o.Repo = Value();
                    break;
                case "layout":
                    o.LayoutDir = Value();
                    break;
                case "harmony-mod":
                    o.Overrides.HarmonyMod = Value();
                    break;
                case "explicit":
                    o.Overrides.Explicit = Flag();
                    break;
                case "rimworld":
                    o.Overrides.RimWorldDir = Value();
                    break;
                case "harmony":
                    o.Overrides.Harmony = Value();
                    break;
                case "fixture":
                    fixtures = Value();
                    break;
                case "rebuild":
                    o.Rebuild = Flag();
                    break;
                case "skip-mod":
                    o.SkipMod = Flag();
                    break;
                case "skip-binaries":
                    _ = Flag();
                    break;
                default:
                    throw new SetupException($"flag provided but not defined: -{name}");
            }
        }

        if (positional.Count > 0)
        {
            throw new SetupException($"setup takes no positional arguments: [{string.Join(' ', positional)}]");
        }

        if (fixtures.Length > 0)
        {
            o.Fixtures = [.. fixtures.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0)];
        }

        if (o.Repo.Length == 0)
        {
            var cwd = Directory.GetCurrentDirectory();
            o.Repo = SetupPackage.FindRepo(cwd) ?? throw new SetupException($"no checkout encloses {cwd}; pass -worktree");
        }
        else if (!Directory.Exists(Path.Combine(o.Repo, ".git")) && !File.Exists(Path.Combine(o.Repo, ".git")))
        {
            throw new SetupException($"-worktree {o.Repo} is not a checkout (no .git)");
        }

        o.Overrides.Repo = o.Repo;
        return true;
    }
}
