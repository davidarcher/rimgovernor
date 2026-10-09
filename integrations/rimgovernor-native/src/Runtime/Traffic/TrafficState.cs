#nullable enable
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Observed pawn traffic per layer: every cell a pawn's path
    // follower enters counts once on the pawn's layer, and on the crossing
    // layer when the step carries soil onto a floor. Counts are not saved
    // (no ExposeData state): a load starts from zero and SinceTick names the
    // window's start so a short window is not read as a quiet colony. The
    // class keeps its old name so saves listing the component still load,
    // and lives in the Assemblies/ runtime: RimGovernor.Host loads the
    // BridgeTools assembly after a save can already be read, so the type
    // database would not resolve it there.
    public sealed class TrafficState : MapComponent
    {
        private const int DecayInterval = 250;
        private static bool installed;
        private static Map? lastMap;
        private static TrafficState? last;
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> followerPawn = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public readonly TrafficCounts Counts;
        public int SinceTick = -1;

        public TrafficState(Map map) : base(map)
        {
            Counts = new TrafficCounts(map.cellIndices.NumGridCells);
            Install();
        }

        public override void MapComponentTick()
        {
            var tick = Find.TickManager.TicksGame;
            if (SinceTick < 0) SinceTick = tick;
            if (tick % DecayInterval == 0) Counts.Decay(DecayInterval);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            new Harmony("rimgovernor.traffic").Patch(AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell"),
                prefix: new HarmonyMethod(typeof(TrafficState), nameof(Before)),
                postfix: new HarmonyMethod(typeof(TrafficState), nameof(After)));
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
