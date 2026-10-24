#nullable enable
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Observed pawn traffic per layer: every cell a pawn's path
    // follower enters counts once on the pawn's layer, and on the crossing
    // layer when the step carries soil onto a floor. Counts are not saved: a
    // load starts from zero. The sighting log is saved (#3835). The class keeps its
    // old name so saves listing the component still load, and lives in the
    // Assemblies/ runtime: RimGovernor.Host loads the
    // BridgeTools assembly after a save can already be read, so the type
    // database would not resolve it there.
    public sealed class TrafficState : MapComponent
    {
        private const int DecayInterval = 250;
        private static bool installed;
        private static Map? lastMap;
        private static TrafficState? last;
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> followerPawn = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<PawnObserver, Pawn> observerPawn = AccessTools.FieldRefAccess<PawnObserver, Pawn>("pawn");

        public readonly TrafficCounts Counts;

        // Each free colonist's own observer passes (#3806), the corpse credit's
        // exposure record; SightingLog states its bound.
        public readonly SightingLog Sightings = new SightingLog();

        private System.Collections.Generic.List<int>? savedSightings;

        public TrafficState(Map map) : base(map)
        {
            Counts = new TrafficCounts(map.cellIndices.NumGridCells);
            Install();
        }

        // ExposeData saves the sighting log (#3835) as SightingLog.Export's
        // flat list; a save without the node loads an empty log.
        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) savedSightings = Sightings.Export();
            Scribe_Collections.Look(ref savedSightings, "sightings", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars) Sightings.Import(savedSightings);
            if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.LoadingVars) savedSightings = null;
        }

        public override void MapComponentTick()
        {
            var tick = Find.TickManager.TicksGame;
            if (tick % DecayInterval == 0) Counts.Decay(DecayInterval);
            if (tick % GenDate.TicksPerHour == 0) RetainColonists();
        }

        // RetainColonists drops the sighting rings of pawns no longer free
        // colonists spawned here (dead, gone, captured).
        public void RetainColonists()
        {
            var here = new System.Collections.Generic.HashSet<int>();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned) here.Add(pawn.thingIDNumber);
            Sightings.Retain(here.Contains);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            var harmony = new Harmony("rimgovernor.traffic");
            harmony.Patch(AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell"),
                prefix: new HarmonyMethod(typeof(TrafficState), nameof(Before)),
                postfix: new HarmonyMethod(typeof(TrafficState), nameof(After)));
            // PawnObserver's pass is private; Harmony names it by string (no typed route reaches a private method).
            harmony.Patch(AccessTools.Method(typeof(PawnObserver), "ObserveSurroundingThings"),
                prefix: new HarmonyMethod(typeof(TrafficState), nameof(Observing)));
        }

        // Observing records a free colonist's pass when it observes at all:
        // ObserveSurroundingThings returns before looking while the pawn is
        // blind, asleep or has no mood (1.7), so those passes see nothing.
        private static void Observing(PawnObserver __instance)
        {
            var pawn = observerPawn(__instance);
            var map = pawn.Map;
            if (map == null || !pawn.IsFreeColonist || PawnUtility.IsBiologicallyOrArtificiallyBlind(pawn) || !pawn.Awake() || pawn.needs?.mood == null) return;
            if (map != lastMap) { last = map.GetComponent<TrafficState>(); lastMap = map; }
            last?.Sightings.Record(pawn.thingIDNumber, Find.TickManager.TicksGame, map.cellIndices.CellToIndex(pawn.Position));
        }

        private static void Before(Pawn_PathFollower __instance, out IntVec3 __state) => __state = followerPawn(__instance).Position;

        // After counts only a real cell change; the lookup is cached per map
        // since every walking pawn lands here.
        private static void After(Pawn_PathFollower __instance, IntVec3 __state)
        {
            var pawn = followerPawn(__instance);
            var map = pawn.Map;
            var cell = pawn.Position;
            if (map == null || cell == __state) return;
            if (map != lastMap) { last = map.GetComponent<TrafficState>(); lastMap = map; }
            if (last == null || !__state.InBounds(map)) return;
            var grid = map.terrainGrid;
            bool crossing = TrafficCounts.IsCrossing(grid.TerrainAt(__state).generatedFilth != null, grid.TerrainAt(cell).IsFloor);
            last.Counts.Step(Layer(pawn), crossing, map.cellIndices.CellToIndex(cell));
        }

        // Layer: free colonists; colony animals; hostiles to the player;
        // other humanlikes outside the colony (visitors, traders). Slaves,
        // prisoners, mechs and wild animals count on no layer of their own.
        private static int Layer(Pawn pawn)
        {
            if (pawn.IsFreeColonist) return TrafficCounts.Colonist;
            var player = Faction.OfPlayer;
            if (pawn.HostileTo(player)) return TrafficCounts.Hostile;
            if (pawn.Faction == player) return pawn.RaceProps.Animal ? TrafficCounts.Animal : -1;
            if (pawn.RaceProps.Humanlike && !pawn.IsPrisoner) return TrafficCounts.Visitor;
            return -1;
        }
    }
}
