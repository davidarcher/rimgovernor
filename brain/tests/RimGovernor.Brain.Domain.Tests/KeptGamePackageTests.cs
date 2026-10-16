using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3293: a kept acceptance game relaunches when any shipped assembly changed (a stale brain otherwise keeps running),
/// while the lab start-cache stamp stays keyed on About.xml, Runtime.dll and Bridge.dll so brain rebuilds add no misses.
/// </summary>
public sealed class KeptGamePackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rg-kept-package-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string LabStampOf(string game)
    {
        var files = new JsonObject();
        foreach (var (name, hash) in Harness.StampFiles(game))
        {
            files[name] = hash;
        }

        return LabStarts.LabStamp(files);
    }

    [Fact]
    public void ChangingOnlyABrainAssemblyRelaunchesWithoutChangingTheLabStamp()
    {
        var game = Path.Combine(root, "game");
        var profile = Path.Combine(root, "profile");
        var configDir = Path.Combine(root, "config");
        Write(Path.Combine("game", "Mods", "RimGovernor", "About", "About.xml"),
            $"<ModMetaData><packageId>{HeadlessProfile.NativePackage}</packageId></ModMetaData>");
        Write(Path.Combine("game", "Mods", "RimGovernor", "Assemblies", "RimGovernor.Runtime.dll"), "runtime");
        Write(Path.Combine("game", "Mods", "RimGovernor", "BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"), "bridge");
        var brain = Write(Path.Combine("game", "Mods", "RimGovernor", "BridgeTools", "RimGovernor", "RimGovernor.Brain.Policy.dll"), "policy v1");
        Write(Path.Combine("profile", "Config", "ModsConfig.xml"),
            $"<ModsConfigData><activeMods><li>ludeon.rimworld</li><li>{HeadlessProfile.NativePackage}</li></activeMods></ModsConfigData>");
        var config = new JsonObject
        {
            ["games"] = new JsonObject
            {
                [HeadlessProfile.TrialGameId] = new JsonObject
                {
                    ["workingDir"] = game,
                    ["args"] = new JsonArray("-savedatafolder=" + profile),
                },
            },
        };
        Write(Path.Combine("config", "config.json"), config.ToJsonString());

        HeadlessProfile.RecordLaunchedMods(configDir);
        HeadlessProfile.RecordLaunchedPackage(configDir);
        Assert.Equal("", HeadlessProfile.LaunchedMismatch(configDir));
        var stamp = LabStampOf(game);

        File.WriteAllText(brain, "policy v2");

        Assert.Equal("package", HeadlessProfile.LaunchedMismatch(configDir));
        Assert.Equal(stamp, LabStampOf(game));
        Assert.Equal(3, Harness.StampFiles(game).Count);
    }
}
