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
}
