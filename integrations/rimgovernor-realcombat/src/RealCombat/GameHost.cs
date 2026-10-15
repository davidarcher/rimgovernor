using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Waylith.Assets;

namespace RimGovernor.RealCombat;

/// <summary>Starts the game's own data and world in a bare process: no window, no Unity, no colony.</summary>
public static class GameHost
{
    internal static Map? EnvironmentMap { get; private set; }
    internal static RealEnvironment? ObservedEnvironment { get; private set; }
    internal static void SetEnvironment(Map map, RealEnvironment? environment) { EnvironmentMap = map; ObservedEnvironment = environment; }

    private const BindingFlags StaticAny = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    /// <summary>Loads every def, language and a generated world. Seeded, so two processes build the same world.</summary>
    public static void Boot(string gameDir)
    {
        var dataDir = Path.Combine(gameDir, "Data");
        var saveDir = Path.Combine(Path.GetTempPath(), "rg-realcombat-" + Environment.ProcessId);
        Directory.CreateDirectory(saveDir);
        SetStatic(typeof(GenFilePaths), "saveDataPath", saveDir);
        SetStatic(typeof(UnityData), "dataPath", dataDir);
        SetStatic(typeof(UnityData), "mainThreadId", Environment.CurrentManagedThreadId);
        SetStatic(typeof(UnityData), "initialized", true);
        SetStatic(typeof(UnityData), "maximumJobWorkerThreads", 4);
        SetStatic(typeof(UnityData), "maximumJobWorkerCount", 4);
        try
        {
            Prefs.Init();
        }
        catch (Exception e)
        {
            // Prefs.Init sets Prefs.data and then fails applying the cursor, which a bare process has none of.
            Console.Error.WriteLine("realcombat: Prefs.Init: " + e.GetBaseException().Message);
        }

        var pack = MountAssets(gameDir, dataDir);
        HostPatches.Install(new Harmony("rimgovernor.realcombat"), pack);
        PlayDataLoader.LoadAllPlayData();
        LanguageDatabase.SelectLanguage(LanguageDatabase.defaultLanguage);
        BuildWorld();
    }

    private static AssetPack MountAssets(string gameDir, string dataDir)
    {
        var zip = Path.Combine(gameDir, "Assets.zip");
        var pack = AssetPack.TryOpen(zip) ?? throw new InvalidOperationException($"realcombat: no asset pack at {zip}");
        var stamps = AssetPackManifest.ReadStamps(zip);
        var contentPack = typeof(AssetPack).Assembly.GetTypes().First(t => t.Name == "ContentPack");
        var use = contentPack.GetMethod("Use", StaticAny) ?? throw new InvalidOperationException("realcombat: ContentPack.Use is gone from this Waylith build");
        use.Invoke(null, [pack, dataDir, stamps]);
        return pack;
    }

    private static void BuildWorld()
    {
        using var scope = new RandScope(111);
        Current.ProgramState = ProgramState.Entry;
        Current.Game = new Game
        {
            InitData = new GameInitData(),
            Scenario = ScenarioDefOf.Crashlanded.scenario,
        };
        Find.Scenario.PreConfigure();
        Current.Game.storyteller = new Storyteller(StorytellerDefOf.Cassandra, DifficultyDefOf.Rough);
        Current.Game.World = WorldGenerator.GenerateWorld(0.05f, "realcombat", OverallRainfall.Normal, OverallTemperature.Normal, OverallPopulation.Normal, LandmarkDensity.Normal);
    }

    /// <summary>Generates the arena map on a settlement tile. Seeded, so two processes build the same map.</summary>
    internal static Map BuildMap(int width, int height)
    {
        if (Find.Maps.Count != 0)
            throw new InvalidOperationException("realcombat: dispose the active arena before constructing another");
        // Construction must not inherit the preceding fight's season/time of day.
        typeof(TickManager).GetField("ticksGameInt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Find.TickManager, 0);
        using var scope = new RandScope(222);
        Current.ProgramState = ProgramState.MapInitializing;
        var tile = TileFinder.RandomSettlementTileFor(Faction.OfPlayer, true);
        var parent = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
        parent.SetFaction(Faction.OfPlayer);
        parent.Tile = tile;
        Find.WorldObjects.Add(parent);
        var map = MapGenerator.GenerateMap(new IntVec3(width, 1, height), parent, parent.MapGeneratorDef, parent.ExtraGenStepDefs, null);
        Current.ProgramState = ProgramState.Playing;
        return map;
    }

    internal static void ReleaseMap(Map map)
    {
        if (ReferenceEquals(EnvironmentMap, map)) { EnvironmentMap = null; ObservedEnvironment = null; }
        var parent = map.Parent;
        Current.Game.DeinitAndRemoveMap(map, false);
        Find.WorldObjects.Remove(parent);
    }

    private static void SetStatic(Type type, string field, object value) =>
        (type.GetField(field, StaticAny) ?? throw new InvalidOperationException($"realcombat: {type.Name}.{field} is gone from this game build")).SetValue(null, value);
}

/// <summary>Pushes a seeded Rand state and pops it on dispose.</summary>
internal readonly struct RandScope : IDisposable
{
    internal RandScope(int seed) => Rand.PushState(seed);

    public void Dispose() => Rand.PopState();
}
