using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3300: a rendered acceptance run writes its launch args into root/config-rendered and leaves the player's
/// root/config/config.json untouched, so the launcher's Play stops inheriting test acceleration and idle exit.
/// </summary>
public sealed class RenderedConfigTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rg-rendered-config-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void PrepareRenderedWritesConfigRenderedAndLeavesThePlayerConfigUntouched()
    {
        var game = Path.Combine(root, "game");
        var profile = Path.Combine(root, "profile");
        Write(Path.Combine("game", "Mods", "RimGovernor", "About", "About.xml"),
            $"<ModMetaData><packageId>{HeadlessProfile.NativePackage}</packageId></ModMetaData>");
        Write(Path.Combine("game", "Mods", "RimGovernor", "Assemblies", "RimGovernor.Runtime.dll"), "runtime");
        Write(Path.Combine("game", "Mods", "RimGovernor", "BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"), "bridge");
        Write(Path.Combine("profile", "Config", "ModsConfig.xml"),
            "<ModsConfigData><activeMods><li>ludeon.rimworld</li></activeMods></ModsConfigData>");
        Write(Path.Combine("profile", "Config", "Prefs.xml"), "<PrefsData>\n</PrefsData>\n");
        var config = new HarnessConfig
        {
            Games = new()
            {
                [HeadlessProfile.TrialGameId] = new HarnessGame
                {
                    LaunchMode = "DirectPath",
                    WorkingDir = game,
                    Args = ["-savedatafolder=" + profile, "-rimgovernor-pause-on-load"],
                },
            },
        };
        var playerConfig = Path.Combine(root, "config", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(playerConfig)!);
        HeadlessProfile.WriteConfig(playerConfig, config);
        var before = File.ReadAllBytes(playerConfig);

        var rendered = HeadlessProfile.PrepareRendered(root, [], []);

        Assert.Equal(Path.Combine(root, "config-rendered"), rendered);
        Assert.Equal(before, File.ReadAllBytes(playerConfig));
        var args = HeadlessProfile.GameSection(HeadlessProfile.LoadConfig(Path.Combine(rendered, "config.json"))).Args!;
        Assert.Contains("-rimgovernor-test-acceleration", args);
        Assert.Contains(HeadlessProfile.IdleExitArg, args);
        Assert.Equal(profile, HeadlessProfile.SaveDataFolder(rendered));
    }
}
