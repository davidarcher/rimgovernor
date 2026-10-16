using System.Globalization;
using RimGovernor.Brain.Acceptance;
using UsageException = RimGovernor.Tools.Common.UsageException;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>
/// Regenerates the thought-to-trigger table (Go: <c>cmd/thoughtaudit</c>): one row per vanilla ThoughtDef with the game
/// state it depends on and how the game grants it, read from the installed game's code.
/// <code>RimGovernor.cmd thoughtaudit [-rimworld dir] [-out table] [-decompiled dir]</code>
/// Needs ilspycmd, a global dotnet tool nothing in the repository pins (<c>dotnet tool install -g ilspycmd</c>). It
/// decompiles Assembly-CSharp.dll (into -decompiled, default a temporary directory; an existing non-empty -decompiled is
/// reused), reads every ThoughtDef under Data/*/Defs and writes -out (default
/// <c>brain/src/RimGovernor.Brain.Policy/thought_triggers.tsv</c>, the file the policy assembly embeds). Situational thoughts (a
/// workerClass) record what the worker reads; memory thoughts record the code and XML sites that grant them. The owner
/// column is hand-maintained: regeneration keeps the owner of every def that remains and leaves new defs unowned. There is
/// no check mode; a stale row simply shows up as unowned.
/// <para>
/// <c>-guards</c> (#3218, a removable spike) instead leaves the table alone and parses the decompiled sources with Roslyn
/// (syntax only) to write draft mechanic cards: each grant site's guard conditions and the writers of the fields they read,
/// with anything unresolved emitted as <c>unknown</c> and a reason. It writes -cards (JSON, the -only defs, default the
/// three Gate 0 benchmarks) and -coverage (TSV, every def) under <c>docs/developers/</c>; see
/// <c>docs/developers/mechanic-card-spike.md</c>. It also writes -shipped (default
/// <c>brain/src/RimGovernor.Brain.Policy/mechanic_cards.json</c>, #3220): the cards of the defs with a live consumer
/// (<see cref="LiveConsumers"/>), which the policy assembly embeds.
/// </para>
/// </summary>
internal static class ThoughtAuditCommand
{
    private const string Usage = "usage: thoughtaudit [-rimworld dir] [-out table] [-decompiled dir] [-guards [-cards file] [-coverage file] [-shipped file] [-only Def,Def]]";

    private static readonly string[] DefaultTable = ["brain", "src", "RimGovernor.Brain.Policy", "thought_triggers.tsv"];

    private static readonly string[] DefaultShipped = ["brain", "src", "RimGovernor.Brain.Policy", "mechanic_cards.json"];

    private static readonly string[] DefaultBenchmarks = ["KnowBuriedInSarcophagus", "AteWithoutTable", "EnvironmentDark"];

    /// <summary>The defs whose cards ship in mechanic_cards.json: those with a live consumer (Gate 1 of #3217: the stranger sarcophagus).</summary>
    private static readonly string[] LiveConsumers = ["KnowBuriedInSarcophagus"];

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string> { "guards" }, new HashSet<string> { "rimworld", "out", "decompiled", "cards", "coverage", "shipped", "only" });
        if (flags.HelpRequested)
        {
            Log.Info(Usage);
            Log.Info($"  -rimworld    RimWorld install directory (default: discovered, or ${SetupDiscovery.RimWorldDirEnv})");
            Log.Info("  -out         table to write (default brain/src/RimGovernor.Brain.Policy/thought_triggers.tsv in the checkout)");
            Log.Info("  -decompiled  directory for the decompiled sources (default: temporary, removed afterwards)");
            Log.Info("  -guards      write draft mechanic cards and the coverage table instead of the trigger table (#3218)");
            Log.Info("  -cards       with -guards: card JSON (default docs/developers/mechanic-card-drafts.json)");
            Log.Info("  -coverage    with -guards: coverage TSV (default docs/developers/mechanic-card-coverage.tsv)");
            Log.Info("  -shipped     with -guards: shipped card file (default brain/src/RimGovernor.Brain.Policy/mechanic_cards.json)");
            Log.Info($"  -only        with -guards: comma-separated defs to write as cards (default {string.Join(',', DefaultBenchmarks)})");
            return 0;
        }
        if (flags.Positional.Count > 0)
        {
            throw new UsageException(Usage);
        }
        var rimworld = GameDirectory.Resolve(flags.String("rimworld"));
        var outPath = flags.String("out");
        if (outPath.Length == 0)
        {
            outPath = Path.Combine([Repo.Require(Directory.GetCurrentDirectory()), .. DefaultTable]);
        }
        if (flags.Bool("guards"))
        {
            var repo = Repo.Require(Directory.GetCurrentDirectory());
            var docs = Path.Combine(repo, "docs", "developers");
            var cards = flags.String("cards", Path.Combine(docs, "mechanic-card-drafts.json"));
            var coverage = flags.String("coverage", Path.Combine(docs, "mechanic-card-coverage.tsv"));
            var shipped = flags.String("shipped", Path.Combine([repo, .. DefaultShipped]));
            var only = flags.String("only").Length > 0 ? flags.String("only").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : DefaultBenchmarks;
            await WithDecompiled(rimworld, flags.String("decompiled"), root => GuardsAsync(rimworld, root, outPath, new GuardPaths(cards, coverage, shipped), only, ct), ct).ConfigureAwait(false);
            return 0;
        }
        await WithDecompiled(rimworld, flags.String("decompiled"), decompiled => AuditAsync(rimworld, outPath, decompiled, ct), ct).ConfigureAwait(false);
        return 0;
    }

    private sealed record GuardPaths(string Cards, string Coverage, string Shipped);

    private static async Task GuardsAsync(string rimworld, string root, string tablePath, GuardPaths paths, string[] only, CancellationToken ct)
    {
        var (defs, xmlFiles) = ThoughtDefs.Load(Path.Combine(rimworld, "Data"));
        var owners = File.Exists(tablePath) ? ThoughtRows.ParseOwners(await File.ReadAllTextAsync(tablePath, ct).ConfigureAwait(false)) : [];
        var files = ThoughtDefs.SortedFiles(root, ".cs").Select(p => (p, File.ReadAllText(p))).ToList();
        var index = SourceIndex.Build(files);
        var cards = new GuardCards(index, root, new HashSet<string>(defs.Select(d => d.Name), StringComparer.Ordinal));
        var all = cards.Build(defs, owners, xmlFiles);
        var game = File.Exists(Path.Combine(rimworld, "Version.txt")) ? (await File.ReadAllTextAsync(Path.Combine(rimworld, "Version.txt"), ct).ConfigureAwait(false)).Trim() : "unknown";
        var chosen = all.Where(c => only.Contains(c.Def)).ToList();
        var live = all.Where(c => LiveConsumers.Contains(c.Def)).ToList();
        if (live.Count != LiveConsumers.Length)
        {
            throw new ToolException("thoughtaudit -guards: a live-consumer def has no card: " + string.Join(',', LiveConsumers.Except(live.Select(c => c.Def))));
        }
        var utf8 = new System.Text.UTF8Encoding(false);
        await File.WriteAllTextAsync(paths.Cards, GuardOutput.Cards(game, GuardOutput.Summarize(all), chosen), utf8, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(paths.Coverage, GuardOutput.Coverage(all), utf8, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(paths.Shipped, GuardOutput.Shipped(game, live), utf8, ct).ConfigureAwait(false);
        GuardTableCheck.Require(paths.Shipped, index);
        Log.Info(string.Create(CultureInfo.InvariantCulture, $"thoughtaudit -guards: {all.Count} defs from {files.Count} sources; wrote {chosen.Count} cards to {paths.Cards}, coverage to {paths.Coverage} and {live.Count} shipped cards to {paths.Shipped}"));
    }

    /// <summary>Runs <paramref name="work"/> on a decompiled tree: the given directory (decompiled into when empty), else a temporary one removed afterwards.</summary>
    private static async Task WithDecompiled(string rimworld, string decompiled, Func<string, Task> work, CancellationToken ct)
    {
        string? temp = null;
        try
        {
            if (decompiled.Length == 0)
            {
                temp = Path.Combine(Path.GetTempPath(), "thoughtaudit-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                decompiled = temp;
            }
            if (!Directory.Exists(decompiled) || !Directory.EnumerateFileSystemEntries(decompiled).Any())
            {
                Directory.CreateDirectory(decompiled);
                var dll = Path.Combine(rimworld, "Assembly-CSharp.dll");
                try
                {
                    await Proc.RunAsync(Directory.GetCurrentDirectory(), "ilspycmd", ["-p", "-o", decompiled, dll], ct).ConfigureAwait(false);
                }
                catch (System.ComponentModel.Win32Exception e)
                {
                    throw new ToolException("ilspycmd not found; install it with: dotnet tool install -g ilspycmd", e);
                }
            }
            await work(decompiled).ConfigureAwait(false);
        }
        finally
        {
            if (temp is not null && Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private static async Task AuditAsync(string rimworld, string outPath, string decompiled, CancellationToken ct)
    {
        var (defs, xmlFiles) = ThoughtDefs.Load(Path.Combine(rimworld, "Data"));
        var names = new HashSet<string>(defs.Select(d => d.Name), StringComparer.Ordinal);
        var (workers, sites) = ThoughtSources.Scan(decompiled, names);
        var rows = ThoughtRows.Build(defs, workers, sites, xmlFiles);
        if (File.Exists(outPath))
        {
            var owners = ThoughtRows.ParseOwners(await File.ReadAllTextAsync(outPath, ct).ConfigureAwait(false));
            rows = [.. rows.Select(r => owners.TryGetValue(r.Def, out var owner) ? r with { Owner = owner } : r)];
        }
        await File.WriteAllTextAsync(outPath, ThoughtRows.Format(rows), ct).ConfigureAwait(false);
        Log.Info(string.Create(CultureInfo.InvariantCulture, $"thoughtaudit: wrote {rows.Count} rows to {outPath}"));
    }
}
