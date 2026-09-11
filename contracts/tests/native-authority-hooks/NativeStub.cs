using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

// Native-shaped fixtures exercise actual Harmony patches, not game simulation.
namespace Verse
{
    public sealed class StaticConstructorOnStartup : Attribute { }
    public static class Log { public static void Error(string text) => Console.Error.WriteLine(text); }
    public static class UnityData { private static readonly int Main = Thread.CurrentThread.ManagedThreadId; public static bool IsInMainThread => Main == Thread.CurrentThread.ManagedThreadId; }
    public class Game
    {
        private Map? map;
        public Map? CurrentMap { get => map; [MethodImpl(MethodImplOptions.NoInlining)] set => map = value; }
        private readonly HomeBridge.BridgeTools.ColonyIdentity identity = new();
        public T? GetComponent<T>() where T : class => identity as T;
        [MethodImpl(MethodImplOptions.NoInlining)] public void UpdatePlay() { }
    }
    public static class Current
    {
        private static Game? game;
        public static Game? Game { get => game; [MethodImpl(MethodImplOptions.NoInlining)] set => game = value; }
    }
    public static class Find { public static Map? CurrentMap => Current.Game?.CurrentMap; }
    public class Map { public int uniqueID; public DesignationManager designationManager = new(); }
    public class DesignationManager
    {
        public readonly List<Designation> Cell = new();
        public readonly List<Designation> Thing = new();
        public IEnumerable<Designation> AllDesignationsAt(IntVec3 c) => Cell;
        public IEnumerable<Designation> AllDesignationsOn(Thing t) => Thing;
    }
    public class Designation { public DesignationDef def = new(); }
    public class DesignationDef { public bool designateCancelable = true; }
    public struct IntVec3 { }
    public struct Rot4 { }
    public class BuildableDef { }
    public class ThingDef { }
    public class Thing { public bool Destroyed; public IntVec3 Position; }
    public class Building : Thing { }
    public class MinifiedThing : Thing { }
    public struct AcceptanceReport { public bool Accepted; }
}
namespace Verse.AI
{
    public class Job { public bool Accepted = true; }
    public enum JobTag { Misc }
    public class Pawn_JobTracker
    { [MethodImpl(MethodImplOptions.NoInlining)] public bool TryTakeOrderedJob(Job job, JobTag? tag = JobTag.Misc, bool requestQueueing = false) => job.Accepted; }
}
namespace RimWorld
{
    using Verse;
    public class Faction { public static readonly Faction OfPlayer = new(); }
    public class Precept_ThingStyle { }
    public class ThingStyleDef { }
    public class Blueprint_Build : Thing { }
    public class Blueprint_Install : Thing { }
    public class Pawn_DraftController
    {
        private bool drafted;
        public bool Drafted { get => drafted; [MethodImpl(MethodImplOptions.NoInlining)] set { if (drafted != value) drafted = value; } }
    }
    public static class GenConstruct
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public static Blueprint_Build PlaceBlueprintForBuild(BuildableDef sourceDef, IntVec3 center, Map map, Rot4 rotation, Faction faction, ThingDef stuff, Precept_ThingStyle? styleSource = null, ThingStyleDef? styleDef = null, bool sendBPSpawnedSignal = true) => new();
        [MethodImpl(MethodImplOptions.NoInlining)] public static Blueprint_Install PlaceBlueprintForInstall(MinifiedThing itemToInstall, IntVec3 center, Map map, Rot4 rotation, Faction faction, bool sendBPSpawnedSignal = true) => new();
        [MethodImpl(MethodImplOptions.NoInlining)] public static Blueprint_Install PlaceBlueprintForReinstall(Building buildingToReinstall, IntVec3 center, Map map, Rot4 rotation, Faction faction, bool sendBPSpawnedSignal = true) => new();
    }
    public class Designator_Cancel
    {
        public Map Map => Find.CurrentMap!;
        public AcceptanceReport CanDesignateThing(Thing t) => new() { Accepted = t is Blueprint_Build || Map.designationManager.Thing.Count > 0 };
        [MethodImpl(MethodImplOptions.NoInlining)] public void DesignateThing(Thing t)
        { if (t is Blueprint_Build) t.Destroyed = true; else Map.designationManager.Thing.RemoveAll(d => d.def.designateCancelable); }
        [MethodImpl(MethodImplOptions.NoInlining)] public void DesignateSingleCell(IntVec3 c) => Map.designationManager.Cell.RemoveAll(d => d.def.designateCancelable);
    }
}
namespace HomeBridge.BridgeTools
{ public class ColonyIdentity { public string ColonyId = "colony"; public string LoadToken = "load"; } }
