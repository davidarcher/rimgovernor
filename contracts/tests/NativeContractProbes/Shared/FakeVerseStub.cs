using System;
using System.Collections.Generic;
using System.IO;
#pragma warning disable CS0649 // Controlled native fixture fields intentionally default to zero/null.

// Unified fake Verse/RimWorld/HomeBridge doubles for the in-process (no real Assembly-CSharp.dll)
// probes: native-authority, native-authority-control, native-authority-status, native-clock-ceiling,
// native-proto-boundary. Reconciled from what were five near-identical, independently-written
// stub files (one per probe) before the NativeContractProbes.csproj consolidation; every field
// and member below is required by at least one of those probes' Program.cs, and none of them
// alter a Check()/Assert() outcome relative to the pre-consolidation per-probe stub it replaces.
namespace Verse
{
    public class Game
    {
        public Map CurrentMap { get => Find.CurrentMap; set => Find.CurrentMap = value; }
        public HomeBridge.BridgeTools.ColonyIdentity Identity = new HomeBridge.BridgeTools.ColonyIdentity();
        public T GetComponent<T>() where T : class => Identity as T;
    }
    public class Map { public int uniqueID; public MapPawns mapPawns = new MapPawns(); }
    public class MapPawns { public List<Pawn> AllPawns = new List<Pawn>(); }
    public class Pawn { public int thingIDNumber; public string GetUniqueLoadID() => "Pawn_" + thingIDNumber; }
    public class Letter { public string Label, Id; public LetterDef def; public string GetUniqueLoadID() => Id; }
    public class LetterDef { public string defName; }
    public static class Current { public static Game Game; }
    public static class Find
    {
        public static Map CurrentMap; public static TickManager TickManager;
        public static List<Map> LoadedMaps;
        // Real Verse exposes the game's loaded maps; the fixtures load one map, the current one.
        public static List<Map> Maps => LoadedMaps ?? (CurrentMap == null ? null : new List<Map> { CurrentMap });
    }
    public enum TimeSpeed { Paused, Normal, Fast, Superfast, Ultrafast }
    public class TickManager
    {
        public int TicksGame;
        public TimeSpeed CurTimeSpeed;
        public bool Forced;
        public bool Paused => Forced || CurTimeSpeed == TimeSpeed.Paused;
        public void TickManagerUpdate() { }
        public void DoSingleTick() { }
    }
    public static class LongEventHandler { public static bool AnyEventNowOrWaiting; }
    public static class Log
    {
        public static void Warning(string text) { } public static void Message(string text) { } public static void Error(string text) { }
        public static void WarningOnce(string text, int key) { } public static void ErrorOnce(string text, int key) { }
    }
    public class Dialog_NodeTree { public int ID; }
    public static class GenFilePaths
    {
        public static string SaveDataFolderPath = Path.Combine(Environment.CurrentDirectory, ".rimgovernor", "clock-tests-" + Guid.NewGuid().ToString("N"));
    }
}
namespace Verse
{
    // The recipe shapes NativeRecipeRoles reads: products, special products, ingredient
    // filters over corpse defs, and the worker counter.
    public class ThingDef { public string defName; public bool IsCorpse; }
    public class ThingDefCountClass { public ThingDef thingDef; }
    public class ThingFilter { public List<ThingDef> AllowedThingDefs = new List<ThingDef>(); }
    public static class DefDatabase<T> where T : class
    {
        public static readonly Dictionary<string, T> Named = new Dictionary<string, T>();
        public static T GetNamedSilentFail(string name) => Named.TryGetValue(name, out var def) ? def : null;
    }
}
namespace RimWorld
{
    public class RecipeWorkerCounter { }
    public class RecipeWorkerCounter_ButcherAnimals : RecipeWorkerCounter { }
    public enum SpecialProductType { None, Butchery, Smelted }
    public class IngredientCount { public Verse.ThingFilter filter = new Verse.ThingFilter(); }
    public class RecipeDef
    {
        public string defName;
        public RecipeWorkerCounter WorkerCounter;
        public List<Verse.ThingDefCountClass> products = new List<Verse.ThingDefCountClass>();
        public List<SpecialProductType> specialProducts;
        public bool mechResurrection;
        public int gestationCycles;
        public List<IngredientCount> ingredients = new List<IngredientCount>();
    }
}
namespace HomeBridge.BridgeTools
{
    // SnapshotStream's native frame hook is outside these in-process probes.
    // The control adapter still requests a capture after changing the view.
    internal static class SnapshotStream
    {
        internal static int CaptureRequests;
        internal static void RequestCapture() => CaptureRequests++;
    }
    public class ColonyIdentity { public string ColonyId = "colony"; public string LoadToken = "load"; }

}
