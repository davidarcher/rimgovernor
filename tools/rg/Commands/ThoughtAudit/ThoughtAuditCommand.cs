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
/// <c>go/internal/policy/thought_triggers.tsv</c>, the file the policy assembly embeds). Situational thoughts (a
/// workerClass) record what the worker reads; memory thoughts record the code and XML sites that grant them. The owner
/// column is hand-maintained: regeneration keeps the owner of every def that remains and leaves new defs unowned. There is
/// no check mode; a stale row simply shows up as unowned.
/// </summary>
internal static class ThoughtAuditCommand
{
    private const string Usage = "usage: thoughtaudit [-rimworld dir] [-out table] [-decompiled dir]";

    private static readonly string[] DefaultTable = ["brain", "src", "RimGovernor.Brain.Policy", "thought_triggers.tsv"];

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string>(), new HashSet<string> { "rimworld", "out", "decompiled" });
        if (flags.HelpRequested)
        {
            Log.Info(Usage);
            Log.Info($"  -rimworld    RimWorld install directory (default: discovered, or ${SetupDiscovery.RimWorldDirEnv})");
            Log.Info("  -out         table to write (default brain/src/RimGovernor.Brain.Policy/thought_triggers.tsv in the checkout)");
            Log.Info("  -decompiled  directory for the decompiled sources (default: temporary, removed afterwards)");
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
        await AuditAsync(rimworld, outPath, flags.String("decompiled"), ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task AuditAsync(string rimworld, string outPath, string decompiled, CancellationToken ct)
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
        finally
        {
            if (temp is not null && Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }
}
