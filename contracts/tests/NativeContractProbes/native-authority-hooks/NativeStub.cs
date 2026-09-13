#nullable enable
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

namespace RimWorld {
 public class WorkTypeDef {}
 public class Pawn_WorkSettings { private int priority; public bool Initialized => true; [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] public int GetPriority(WorkTypeDef w) => priority; [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] public void SetPriority(WorkTypeDef w, int value) { priority = value; } }
}

namespace UnityEngine { public class Event {} }
namespace Verse {
 public class Zone {
 public List<IntVec3> Cells = new();
 [MethodImpl(MethodImplOptions.NoInlining)] public void AddCell(IntVec3 c) {if(!Cells.Contains(c))Cells.Add(c);}
 [MethodImpl(MethodImplOptions.NoInlining)] public void RemoveCell(IntVec3 c) {Cells.Remove(c);}
 }
 public class ZoneManager {
 public List<Zone> AllZones=new();
 [MethodImpl(MethodImplOptions.NoInlining)] public void DeregisterZone(Zone zone) {AllZones.Remove(zone);}
 }
 public class Command_Toggle { public Action toggleAction=()=>{};
 [MethodImpl(MethodImplOptions.NoInlining)] public void ProcessInput(UnityEngine.Event e){toggleAction();}
 }
}
namespace RimWorld {
 public class Zone_Growing : Verse.Zone {
 private Verse.ThingDef? plantDefToGrow;
 [MethodImpl(MethodImplOptions.NoInlining)] public void SetPlantDefToGrow(Verse.ThingDef crop){plantDefToGrow=crop;}
 }
}

namespace UnityEngine { public struct Rect { } }
namespace Verse {
 public class Pawn { }
 public struct FloatRange { public float min; public float max; }
 public struct IntRange { public int min; public int max; }
 public class ThingFilter { public string Summary = ""; }
}
namespace RimWorld {
 using Verse;
 public class BillRepeatModeDef { public string defName = "Forever"; }
 public class BillStoreModeDef { public string defName = "DropOnFloor"; }
 public interface ISlotGroup { }
 public class QualityRange { public int min; public int max; }
 public class Bill {
  public bool suspended;
  public ThingFilter ingredientFilter = new();
  public float ingredientSearchRadius = 999f;
  public IntRange allowedSkillRange;
  public Pawn? PawnRestriction;
  public bool SlavesOnly;
  public bool MechsOnly;
  public bool NonMechsOnly;
  // Simulates the real DoInterface's inline suspend-toggle click handler firing mid-call.
  public Action? interfaceAction;
  [MethodImpl(MethodImplOptions.NoInlining)] public virtual UnityEngine.Rect DoInterface(float x, float y, float width, int index) { interfaceAction?.Invoke(); return default; }
 }
 public class Bill_Production : Bill {
  public BillRepeatModeDef? repeatMode = new();
  public int repeatCount = 1;
  public int targetCount = 1;
  public bool pauseWhenSatisfied;
  public int unpauseWhenYouHave;
  public bool includeEquipped;
  public bool includeTainted;
  public FloatRange hpRange;
  public QualityRange qualityRange = new();
  public bool limitToAllowedStuff;
  private ISlotGroup? includeGroup;
  private BillStoreModeDef? storeMode;
  private ISlotGroup? storeGroup;
  public ISlotGroup? GetIncludeSlotGroup() => includeGroup;
  public void SetIncludeGroup(ISlotGroup? group) => includeGroup = group;
  public BillStoreModeDef? GetStoreMode() => storeMode;
  public ISlotGroup? GetSlotGroup() => storeGroup;
  public void SetStoreMode(BillStoreModeDef mode, ISlotGroup? group) { storeMode = mode; storeGroup = group; }
 }
 public class BillStack {
  public readonly List<Bill> bills = new();
  [MethodImpl(MethodImplOptions.NoInlining)] public void AddBill(Bill bill) => bills.Add(bill);
  [MethodImpl(MethodImplOptions.NoInlining)] public void Delete(Bill bill) => bills.Remove(bill);
  [MethodImpl(MethodImplOptions.NoInlining)] public void Reorder(Bill bill, int offset)
  { var i = bills.IndexOf(bill); if (i < 0) return; bills.RemoveAt(i); var to = i + offset; if (to < 0) to = 0; if (to > bills.Count) to = bills.Count; bills.Insert(to, bill); }
 }
 public class Dialog_BillConfig {
  protected Bill_Production bill;
  // Simulates whatever field write the real config dialog would perform this frame.
  public Action? editAction;
  public Dialog_BillConfig(Bill_Production bill) { this.bill = bill; }
  [MethodImpl(MethodImplOptions.NoInlining)] public void DoWindowContents(UnityEngine.Rect inRect) => editAction?.Invoke();
 }
}
