using System.Security.Cryptography;
using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Which fixtures a native build records, and which a build serving every case sets.</summary>
public sealed class NativeModBuildTests : IDisposable
{
    private readonly string repo = Path.Combine(Path.GetTempPath(), "rg-native-build-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(repo))
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void AFixtureBuildCarriesTheAlwaysFixturesAndAProductionBuildNone()
    {
        Assert.Empty(NativeModBuild.RecordedFixtures([]));
        Assert.Equal(
            ["DebugStartFixture", "FreezeNeedsFixture", "LedgerFixture", "LetterFixture", "QuietStorytellerFixture"],
            NativeModBuild.RecordedFixtures(["LedgerFixture", "LetterFixture", "LedgerFixture"]));
    }

    /// <summary>
    /// A source compiled under another source's flag (ArrestFixture under MoodFixture) is no flag of its own; both flags
    /// of an Or condition are.
    /// </summary>
    [Fact]
    public void EveryFixtureMeansEveryBuildFlagOfTheNativeProject()
    {
        var project = Path.Combine(repo, FixtureInputs.NativeProject.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, """
            <Project>
              <ItemGroup>
                <Compile Include="../../../../scripts/fixtures/MoodFixture.cs" Condition="'$(MoodFixture)' == 'true'" />
                <Compile Include="../../../../scripts/fixtures/ArrestFixture.cs" Condition="'$(MoodFixture)' == 'true'" />
                <Compile Include="../../../../scripts/fixtures/LedgerFixture.cs" Condition="'$(LedgerFixture)' == 'true'" />
                <Compile Include="../../../../scripts/fixtures/TrainingFixture.cs" Condition="'$(TrainingFixture)' == 'true' Or '$(PawnControlFixture)' == 'true'" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(["LedgerFixture", "MoodFixture", "PawnControlFixture", "TrainingFixture"], FixtureInputs.BuildFlags(repo));
    }

    /// <summary>
    /// The work sync rewrites, with the current time, a file it did not itself last write, even when its content
    /// already matches: build_native_mod.ps1 left such files with the source's old timestamp beside outputs built from
    /// other content. A file the sync wrote and nobody changed keeps its timestamp.
    /// </summary>
    [Fact]
    public void TheWorkSyncRewritesWhatItDidNotWriteAndKeepsWhatItDid()
    {
        var source = Path.Combine(repo, "source");
        var work = Path.Combine(repo, "work");
        _ = Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "A.cs"), "class A {}");
        var copied = new Dictionary<string, string> { ["A.cs"] = Convert.ToHexStringLower(SHA256.HashData("class A {}"u8)) };
        _ = Directory.CreateDirectory(work);
        var target = Path.Combine(work, "A.cs");
        File.WriteAllText(target, "class A {}");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, old);

        NativeModBuild.SyncWork(source, work, copied);
        var synced = File.GetLastWriteTimeUtc(target);
        Assert.True(synced > old);

        NativeModBuild.SyncWork(source, work, copied);
        Assert.Equal(synced, File.GetLastWriteTimeUtc(target));
    }

    /// <summary>
    /// Two worktrees whose contracts differ share one work directory (#3430): the second build must not inherit the
    /// first's obj/bin/_out. The same contracts keep the state; non-contract changes do not discard it.
    /// </summary>
    [Theory]
    [InlineData("GameFacts")]
    [InlineData("GameOrders")]
    public void BuildStateSurvivesOnlyWhileTheContractsAreUnchanged(string project)
    {
        var work = Path.Combine(repo, "work");
        var contract = $"brain/src/RimGovernor.Brain.{project}/Operations.cs";
        var a = new Dictionary<string, string> { [contract] = "aa", ["brain/src/RimGovernor.Brain.Domain/X.cs"] = "11" };
        var obj = Path.Combine(work, "brain", "src", $"RimGovernor.Brain.{project}", "obj", "stale.dll");
        void Build()
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(obj)!);
            File.WriteAllText(obj, "x");
            File.WriteAllText(Path.Combine(work, "_out", "published.txt"), "x");
        }

        Assert.False(NativeModBuild.DiscardBuildStateOnContractChange(work, a));
        Build();
        Assert.False(NativeModBuild.DiscardBuildStateOnContractChange(work, a));
        Assert.True(File.Exists(obj));

        var otherNonContract = new Dictionary<string, string>(a) { ["brain/src/RimGovernor.Brain.Domain/X.cs"] = "22" };
        Assert.False(NativeModBuild.DiscardBuildStateOnContractChange(work, otherNonContract));
        Assert.True(File.Exists(obj));

        var otherContracts = new Dictionary<string, string>(a) { [contract] = "bb" };
        Assert.True(NativeModBuild.DiscardBuildStateOnContractChange(work, otherContracts));
        Assert.False(File.Exists(obj));
        Assert.False(File.Exists(Path.Combine(work, "_out", "published.txt")));
        Assert.False(NativeModBuild.DiscardBuildStateOnContractChange(work, otherContracts));
    }

    /// <summary>
    /// The production check (#3393) reads an assembly's fixture surface from metadata: the contracts assembly carries
    /// its records, an assembly with a <c>[Tool("test/...")]</c> op carries that op, and the harness assembly neither.
    /// </summary>
    [Fact]
    public void TheFixtureSurfaceIsTheContractsTypesAndTheTestOps()
    {
        Assert.Contains(
            "RimGovernor.Fixtures.Contracts.StageSpec",
            NativeModBuild.FixtureSurface(typeof(global::RimGovernor.Fixtures.Contracts.StageSpec).Assembly.Location));
        Assert.Contains("test/probe", NativeModBuild.FixtureSurface(typeof(NativeModBuildTests).Assembly.Location));
        Assert.Empty(NativeModBuild.FixtureSurface(typeof(NativeModBuild).Assembly.Location));
    }

    [Tool("test/probe")]
    internal static void Probe()
    {
    }

    /// <summary>Stands in for the host SDK's tool attribute, which the check matches by name.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class ToolAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }
}
