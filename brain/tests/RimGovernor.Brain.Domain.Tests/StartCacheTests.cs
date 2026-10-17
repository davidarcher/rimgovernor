using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The cached lab and debug starts missed on every run (#3292): a case with no expansions got every installed DLC
/// activated while the cache compared against "", and worker roots started without the cache saves.
/// </summary>
public sealed class StartCacheTests : IDisposable
{
    private static readonly string[] AllDlc =
    [
        "ludeon.rimworld.royalty",
        "ludeon.rimworld.ideology",
        "ludeon.rimworld.biotech",
        "ludeon.rimworld.anomaly",
        "ludeon.rimworld.odyssey",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rg-startcache-" + Guid.NewGuid().ToString("N"));

    public StartCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NullExpansionsResolveToTheProfilesActiveDlcSoAnAllDlcSaveIsNotStale()
    {
        // What Prepare does for a case and environment that leave Expansions null: every installed expansion.
        var modsConfig = Path.Combine(_dir, "ModsConfig.xml");
        File.WriteAllText(modsConfig, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModsConfigData><version>1.6</version><activeMods><li>ludeon.rimworld</li></activeMods><knownExpansions /></ModsConfigData>");
        HeadlessProfile.PrepareNativeModConfig(modsConfig, [.. AllDlc.Order(StringComparer.Ordinal)], [.. AllDlc.Order(StringComparer.Ordinal)]);
        var save = WriteSave("RimGovernor-lab-100", AllDlc);

        var active = HeadlessProfile.ActiveExpansions(modsConfig);
        var cache = new ProfileStartCache();
        cache.Configure(_dir, headless: true, active);

        Assert.Equal(AllDlc, active);
        Assert.False(cache.IsStale(save, "RimGovernor-lab-100"));

        // The old configuration, the unresolved request as "", is stale against the same save.
        cache.Configure(_dir, headless: true, []);
        Assert.True(cache.IsStale(save, "RimGovernor-lab-100"));
    }

    [Fact]
    public void AllDlcNamesUseShortExpansionNamesAndFitTheSaveNameLimit()
    {
        var cache = new ProfileStartCache();
        cache.Configure(_dir, headless: true, AllDlc);

        Assert.Equal("RimGovernor-lab-100-royalty-ideology-biotech-anomaly-odyssey", LabStarts.LabCacheName(cache, LabWorld.DefaultLabColonist));
        Assert.Equal("RimGovernor-lab-100-royalty-ideology-biotech-anomaly-odyssey-c5", LabStarts.LabCacheName(cache, 5));
        var longLab = LabStarts.LabCacheName(cache, 12345);
        Assert.Equal(ProfileStartCache.MaxSaveName, longLab.Length);
        Assert.StartsWith(LabStarts.LabPrefix, longLab, StringComparison.Ordinal);

        var debug = cache.NameFor(new DebugStartSize { MapSize = 250, PlanetCoverage = 0.3, Biomes = "TemperateForest" }, quiet: true);
        Assert.True(debug.Length <= ProfileStartCache.MaxSaveName, debug);
        Assert.StartsWith("RimGovernor-debug-250-0_3-royalty-ideology", debug, StringComparison.Ordinal);
        Assert.Equal(debug, cache.NameFor(new DebugStartSize { MapSize = 250, PlanetCoverage = 0.3, Biomes = "TemperateForest" }, quiet: true));

        // A case's short names and full package ids name the same save.
        var shortNames = new ProfileStartCache();
        shortNames.Configure(_dir, headless: true, ["royalty", "ideology", "biotech", "anomaly", "odyssey"]);
        Assert.Equal(LabStarts.LabCacheName(cache, 5), LabStarts.LabCacheName(shortNames, 5));
    }

    [Fact]
    public void IsolatedRootCopiesTheLabAndDebugCacheFiles()
    {
        var source = Path.Combine(_dir, "source");
        Directory.CreateDirectory(Path.Combine(source, "config"));
        var config = new HarnessConfig
        {
            Games = new()
            {
                [HeadlessProfile.TrialGameId] = new HarnessGame { LaunchMode = "DirectPath", WorkingDir = _dir },
            },
        };
        HeadlessProfile.WriteConfig(Path.Combine(source, "config", "config.json"), config);
        Directory.CreateDirectory(Path.Combine(source, "profile", "Config"));
        File.WriteAllText(Path.Combine(source, "profile", "Config", "Prefs.xml"), "<PrefsData />");
        File.WriteAllText(Path.Combine(source, "profile", "Config", "ModsConfig.xml"), "<ModsConfigData />");
        var saves = Path.Combine(source, "profile", "Saves");
        Directory.CreateDirectory(saves);
        string[] cached =
        [
            "RimGovernor-lab-100-royalty.rws",
            "RimGovernor-lab-100-royalty.stamp",
            "RimGovernor-lab-100-royalty.lab.json",
            "RimGovernor-debug-200-0_05-quiet.rws",
        ];
        string[] other = ["Colony.rws", "RimGovernor-debug-200-0_05-quiet.rws.bak"];
        foreach (var file in cached.Concat(other))
        {
            File.WriteAllText(Path.Combine(saves, file), file);
        }

        var worker = HeadlessProfile.IsolatedRoot(source, Path.Combine(_dir, "worker"));

        foreach (var file in cached)
        {
            Assert.Equal(file, File.ReadAllText(Path.Combine(worker, "profile", "Saves", file)));
        }

        foreach (var file in other)
        {
            Assert.False(File.Exists(Path.Combine(worker, "profile", "Saves", file)), file);
        }
    }

    private string WriteSave(string name, IEnumerable<string> expansions)
    {
        var path = Path.Combine(_dir, name + ".rws");
        var mods = "<li>brrainz.harmony</li><li>ludeon.rimworld</li>" + string.Concat(expansions.Select(id => $"<li>{id}</li>"));
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<savegame><meta><gameVersion>1.6</gameVersion><modIds>{mods}</modIds></meta></savegame>");
        return path;
    }
}
