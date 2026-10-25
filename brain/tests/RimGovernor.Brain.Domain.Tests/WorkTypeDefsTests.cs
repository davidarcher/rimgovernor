using System.Text.RegularExpressions;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The Observation reader names a native WorkTypeDef through <see cref="WorkType"/>; a def the mod itself ships that the
/// enum lacks failed every ReviewState pass (#3136). Reads the mod's own WorkTypeDefs against the enum's wire names.
/// </summary>
public sealed partial class WorkTypeDefsTests
{
    private const string DefsDirectory = "integrations/rimgovernor-native/Defs/WorkTypeDefs";

    [GeneratedRegex("<defName>\\s*([^<\\s]+)\\s*</defName>")]
    private static partial Regex DefName();

    [Fact]
    public void EveryWorkTypeTheModShipsIsAWorkTypeMember()
    {
        var root = RepositoryRoot();
        var names = Directory.GetFiles(Path.Combine(root, DefsDirectory), "*.xml")
            .SelectMany(file => DefName().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .ToList();
        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            Assert.True(Wire.TryParse<WorkType>(name, out var work) && work != WorkType.Unrecognized, $"WorkType has no member for the mod's work type {name}");
        }
    }

    [Fact]
    public void AnUnknownWorkTypeNameIsNotAMember() => Assert.False(Wire.TryParse<WorkType>("SomeOtherModWork", out _));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName, DefsDirectory)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"no repository root above {AppContext.BaseDirectory}");
    }
}
