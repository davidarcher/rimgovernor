using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3190: a case launch asks the game's CoreCLR for a minidump into the case output, a kept process is not relaunched
/// for the per-case dump path, and the postmortem names the dump and the game.log tail after the startup crash.
/// </summary>
public sealed class CrashDumpTests : IDisposable
{
    private const string StartupCrash =
        "connect: the game (pid 4242) exited before its channel came up (exit code -1073741819): game.log ends with: Fatal error. Internal CLR error. (0x80131506)";

    private readonly string root = Path.Combine(Path.GetTempPath(), "rg-crashdump-" + Guid.NewGuid().ToString("N"));

    public CrashDumpTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceCase Case(string name) => new TypedCase
    {
        Name = name,
        Scope = "crash dump launch env",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Run = (_, _) => Task.CompletedTask,
    }.ToCase();

    [Fact]
    public void CaseLaunchEnvDumpsIntoTheCaseOutput()
    {
        var output = Path.Combine(root, "out", "floor", "kitchen");
        var cfg = CaseRunner.NewConfig(Case("floor/kitchen"), new RunOptions { Root = root }, output);

        Assert.Equal("1", cfg.LaunchEnv[CrashDump.EnableVariable]);
        Assert.Equal(CrashDump.MiniType, cfg.LaunchEnv[CrashDump.TypeVariable]);
        Assert.Equal(Path.Combine(output, "coreclr-%p.dmp"), cfg.LaunchEnv[CrashDump.NameVariable]);
    }

    [Fact]
    public void AnotherCasesDumpPathDoesNotRelaunchAKeptGame()
    {
        var configDir = Path.Combine(root, "config");
        Directory.CreateDirectory(configDir);
        var config = new JsonObject
        {
            ["games"] = new JsonObject
            {
                [HeadlessProfile.TrialGameId] = new JsonObject { ["args"] = new JsonArray("-savedatafolder=" + Path.Combine(root, "profile")) },
            },
        };
        File.WriteAllText(Path.Combine(configDir, "config.json"), config.ToJsonString());
        var first = CaseRunner.NewConfig(Case("a/one"), new RunOptions { Root = root }, Path.Combine(root, "a", "one"));
        HeadlessProfile.RecordLaunchedEnv(configDir, first.LaunchEnv);

        var second = CaseRunner.NewConfig(Case("a/two"), new RunOptions { Root = root }, Path.Combine(root, "a", "two"));
        Assert.Equal("", HeadlessProfile.LaunchedEnvMismatch(configDir, second.LaunchEnv));

        second.LaunchEnv[CrashDump.TypeVariable] = "4";
        Assert.Equal("env", HeadlessProfile.LaunchedEnvMismatch(configDir, second.LaunchEnv));
    }

    [Fact]
    public void PostmortemNamesTheDumpAndTheLogTailAfterAStartupCrash()
    {
        File.WriteAllBytes(Path.Combine(root, "coreclr-4242.dmp"), new byte[64]);
        var log = Enumerable.Range(1, 20).Select(i => string.Create(CultureInfo.InvariantCulture, $"line {i}")).Append("Fatal error. Internal CLR error. (0x80131506)");
        File.WriteAllLines(Path.Combine(root, GameLogCapture.GameLogName), log);

        var digest = Postmortems.Collect(root, new JsonObject { ["case"] = "floor/kitchen", ["error"] = StartupCrash }, TestContext.Current.CancellationToken);

        var section = Assert.Single(digest.Sections, s => s.Name == "crash dump");
        Assert.Equal("", section.Note);
        Assert.Contains(section.Lines, l => l.Evidence == "coreclr-4242.dmp" && l.Text.Contains("64 bytes", StringComparison.Ordinal));
        var tail = section.Lines.Where(l => l.Evidence.StartsWith("game.log#", StringComparison.Ordinal)).ToList();
        Assert.Equal(PostmortemCrash.LogTailLines, tail.Count);
        Assert.Equal("game.log#21", tail[^1].Evidence);
        Assert.Contains("Internal CLR error", tail[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupCrashWithoutADumpSaysSo()
    {
        var section = PostmortemCrash.CrashDumps(root, StartupCrash);

        Assert.Contains("without a dump", section.Note, StringComparison.Ordinal);
        Assert.Contains(section.Lines, l => l.Text.Contains("no readable game.log", StringComparison.Ordinal));
    }

    [Fact]
    public void OtherFailuresGetNoLogTail()
    {
        File.WriteAllLines(Path.Combine(root, GameLogCapture.GameLogName), ["boom"]);

        var section = PostmortemCrash.CrashDumps(root, "connect: the game (pid 1) exited before its channel came up (exit code 7)");

        Assert.Empty(section.Lines);
        Assert.Contains(CrashDump.FilePattern, section.Note, StringComparison.Ordinal);
    }
}
