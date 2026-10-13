using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>acceptance suite</c> (Go <c>cmd/acceptance/suite.go</c>): runs a set of cases across N private game copies at once.
/// Each worker is its own disposable root (<see cref="HeadlessProfile.IsolatedRoot"/> off <c>-root</c>, so its own config
/// and profile) launching the same game binary, chains its cases on one kept process (<see cref="Game.KeepGameEnv"/>
/// forced on) and stops it when its queue is empty. Every case keeps its own output directory and result.json; the suite's
/// result.json lists them with exit code, wall and boot time, and passes only when every case did.
/// <para>
/// The set is the registry (<c>-all</c>), registry names (<c>-cases a,b</c>), a JSON suite file (<c>-suite</c>) whose rows
/// name registry cases, or a tier (<c>-tier nightly|smoke</c>, <see cref="Tiers"/>; the report records "tier"); every row
/// runs through <c>acceptance run</c>. "acceptance" labels the criterion a row stands for and is echoed into its report
/// row. Every row runs fresh (<c>-fresh</c>, no checkpoint ring) unless <c>-resume</c>, which carries each case's
/// checkpoint ring from <c>-root</c> into its worker (the ring a failed <c>acceptance run</c> in that root left) so the row
/// resumes as the run would: the row passes and carries resumed_from, and the report lists "resumed". Without
/// <c>-resume</c> a row whose result.json carries resumed_from fails the suite. A resumed pass proves the fix only past the
/// resume point; the landing records it rather than refusing it.
/// </para>
/// <para>
/// Scheduling and baseline/regression/drift flagging are described on <see cref="SuiteRunner"/>.
/// </para>
/// </summary>
public static class SuiteCommand
{
    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance suite (-all | -cases a,b,... | -suite file.json | -tier nightly|smoke) -root <dir> -output <dir> [-workers N -baseline <result.json> -series <metrics.jsonl> -no-series -game <id> -timeout <d> -case-timeout <d> -budget <d> -stall <d> -evidence capped|full -resume -stages]
            -tier nightly runs the end-to-end cases, smoke the runner-proving set (acceptance list -tier <name> prints a tier)
        """;

    /// <summary>
    /// Runs the subcommand with the arguments after <c>suite</c>; returns the process exit code (2 usage, 1 a failed
    /// suite).
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CaseRegistry registry,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        IReadOnlyList<SuiteEntry> list;
        SuiteOptions opts;
        try
        {
            (list, opts) = Parse(args, registry);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        return await SuiteRunner.RunSuiteAsync(list, opts, stderr, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the suite subcommand's flags into the list of rows (in file order, before scheduling) and the options.</summary>
    public static (IReadOnlyList<SuiteEntry> Rows, SuiteOptions Options) Parse(IReadOnlyList<string> args, CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        var fs = new FlagSet("suite");
        var all = fs.Bool("all", false);
        var names = fs.String("cases", "");
        var suite = fs.String("suite", "");
        var tier = fs.String("tier", "");
        var root = fs.String("root", "");
        var output = fs.String("output", "");
        var baseline = fs.String("baseline", "");
        var series = fs.String("series", "");
        var noSeries = fs.Bool("no-series", false);
        var game = fs.String("game", "rimgovernor-trial");
        var workers = fs.Int("workers", 2);
        var timeout = fs.Duration("timeout", TimeSpan.FromHours(2));
        var caseTimeout = fs.Duration("case-timeout", TimeSpan.Zero);
        var budget = fs.Duration("budget", TimeSpan.Zero);
        var stall = fs.Duration("stall", TimeSpan.Zero);
        var evidence = fs.String("evidence", "");
        var resume = fs.Bool("resume", false);
        var stages = fs.Bool("stages", false);
        fs.Parse(args);
        if (fs.Args.Count > 0)
        {
            throw new UsageException($"unexpected arguments: [{string.Join(' ', fs.Args)}]");
        }

        var selectors = new[] { all.Value, names.Value.Length > 0, suite.Value.Length > 0, tier.Value.Length > 0 }.Count(set => set);
        if (selectors != 1)
        {
            throw new UsageException("exactly one of -all, -cases, -suite or -tier is required");
        }

        if (root.Value.Length == 0 || output.Value.Length == 0)
        {
            throw new UsageException("-root and -output are required");
        }

        if (!Path.IsPathFullyQualified(root.Value))
        {
            throw new UsageException($"-root must be absolute: {root.Value}");
        }

        if (workers.Value < 1)
        {
            throw new UsageException("-workers must be at least 1");
        }

        if (stages.Value && resume.Value)
        {
            throw new UsageException("-stages and -resume are exclusive: a ring resume is later on the timeline than any stage");
        }

        var fullOutput = Path.GetFullPath(output.Value);
        var seriesPath = Path.GetFullPath(series.Value.Length == 0 ? MetricSeries.SeriesPath(fullOutput) : series.Value);
        var list = new List<SuiteEntry>();
        if (all.Value)
        {
            list.AddRange(Tiers.Tiered(registry).Select(c => new SuiteEntry(c.Name)));
        }
        else if (names.Value.Length > 0)
        {
            foreach (var name in names.Value.Split(','))
            {
                var trimmed = name.Trim();
                if (trimmed.Length > 0)
                {
                    list.Add(new SuiteEntry(trimmed));
                }
            }
        }
        else if (tier.Value.Length > 0)
        {
            list.AddRange(Tiers.CasesOf(registry, tier.Value).Select(c => new SuiteEntry(c.Name)));
            if (list.Count == 0)
            {
                throw new UsageException($"tier {tier.Value} selects no case");
            }
        }
        else
        {
            list.AddRange(ReadSuiteFile(suite.Value));
        }

        var opts = new SuiteOptions
        {
            Root = root.Value,
            Output = fullOutput,
            Baseline = baseline.Value,
            GameId = game.Value,
            Series = seriesPath,
            NoSeries = noSeries.Value,
            Workers = workers.Value,
            Timeout = timeout.Value,
            CaseTimeout = caseTimeout.Value,
            Budget = budget.Value,
            Stall = stall.Value,
            Evidence = evidence.Value,
            Tier = tier.Value,
            Resume = resume.Value,
            Stages = stages.Value,
        };
        return (ResolveEntries(list, registry), opts);
    }

    /// <summary>Reads a suite file: a JSON array of <c>{name, acceptance}</c> rows.</summary>
    public static IReadOnlyList<SuiteEntry> ReadSuiteFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException e)
        {
            throw new UsageException(e.Message, e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new UsageException(e.Message, e);
        }

        return ParseSuiteText(text);
    }

    /// <summary>Parses suite-file text (an array of rows); used for the embedded suites as well.</summary>
    public static IReadOnlyList<SuiteEntry> ParseSuiteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new UsageException($"-suite must be a JSON array of {{name, acceptance}}: {e.Message}", e);
        }

        if (root is not JsonArray rows)
        {
            throw new UsageException("-suite must be a JSON array of {name, acceptance}: not an array");
        }

        var list = new List<SuiteEntry>();
        foreach (var row in rows)
        {
            if (row is not JsonObject o)
            {
                throw new UsageException("-suite must be a JSON array of {name, acceptance}: a row is not an object");
            }

            list.Add(new SuiteEntry(o["name"].AsString(), o["acceptance"].AsString()));
        }

        return list;
    }

    /// <summary>Binds each row to its registry case and rejects unknown, duplicate or empty names.</summary>
    public static IReadOnlyList<SuiteEntry> ResolveEntries(IReadOnlyList<SuiteEntry> list, CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(registry);
        if (list.Count == 0)
        {
            throw new UsageException("no cases listed");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<SuiteEntry>(list.Count);
        foreach (var e in list)
        {
            if (e.Name.Length == 0 || !seen.Add(e.Name))
            {
                throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"case names must be unique and non-empty, got \"{e.Name}\""));
            }

            var c = registry.Lookup(e.Name)
                ?? throw new UsageException($"unknown case \"{e.Name}\" (see `acceptance list`)");
            resolved.Add(e with { Registered = c });
        }

        return resolved;
    }
}
