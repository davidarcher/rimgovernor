using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimGovernor.Native
{
    // Observed hostile arrivals: every SampleInterval ticks each lord
    // of a faction hostile to the player is looked up; the first time one is
    // seen, the position of its first spawned pawn is its spawn cell (ground
    // when that cell lies within EdgeMargin of the map edge: a walk-in raid
    // spawns up to eight cells inside its edge cell and walks a few more
    // before the first sample, while drop pods and tunnellers land at the
    // colony). Afterwards the pawn of
    // the lord nearest the colony adds one trail sample, so the controller
    // can find where the raid crossed the boundary of its own census rather
    // than guess from a far map-edge coordinate. The tracks are RaidTrackLog's:
    // a live lord keeps its whole trail, a lord unseen for a day is finished and
    // only the newest finished ground tracks stay. ExposeData saves them with the
    // map (RG-HABITAT-009); a save without the nodes loads none.
    public sealed class RaidArrivalState : MapComponent
    {
        public const int SampleInterval = 60;
        public const int EdgeMargin = 14;

        public readonly RaidTrackLog Tracks = new RaidTrackLog();

        private List<int>? savedTracks;
        private List<string>? savedFactions;
        private List<float>? savedRanges;

        public RaidArrivalState(Map map) : base(map) { }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) (savedTracks, savedFactions, savedRanges) = Tracks.Export();
            Scribe_Collections.Look(ref savedTracks, "raidTracks", LookMode.Value);
            Scribe_Collections.Look(ref savedFactions, "raidFactions", LookMode.Value);
            Scribe_Collections.Look(ref savedRanges, "raidRanges", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars) Tracks.Import(savedTracks, savedFactions, savedRanges);
            if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.LoadingVars) (savedTracks, savedFactions, savedRanges) = (null, null, null);
        }

        public override void MapComponentTick()
        {
            var tick = Find.TickManager.TicksGame;
            if (tick % SampleInterval != 0) return;
            var player = Faction.OfPlayerSilentFail;
            if (player == null) return;
            var home = map.areaManager?.Home;
            var center = home != null && home.TrueCount > 0 ? Centroid(home) : map.Center;
            foreach (var lord in map.lordManager.lords)
            {
                if (lord.faction == null || !lord.faction.HostileTo(player)) continue;
                var pawns = lord.ownedPawns.Where(p => p != null && p.Spawned && !p.Dead && p.Map == map).ToList();
                if (pawns.Count == 0) continue;
                var first = pawns[0].Position;
                var nearest = pawns.OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber).First().Position;
                Tracks.Sample(lord.loadID, lord.faction.def.defName, tick, (first.x, first.z), OnEdge(first), (nearest.x, nearest.z), pawns.Max(WeaponRange));
            }
            Tracks.Evict(tick);
        }

        // A pawn's primary verb range (Pawn_EquipmentTracker.PrimaryEq.PrimaryVerb): 0 without one or for a melee verb.
        private static float WeaponRange(Pawn p)
        {
            var verb = p.equipment?.PrimaryEq?.PrimaryVerb;
            return verb == null || verb.IsMeleeAttack ? 0f : verb.verbProps.range;
        }

        private bool OnEdge(IntVec3 c) => c.x < EdgeMargin || c.z < EdgeMargin || c.x >= map.Size.x - EdgeMargin || c.z >= map.Size.z - EdgeMargin;

        private static IntVec3 Centroid(Area area)
        {
            long x = 0, z = 0, n = 0;
            foreach (var c in area.ActiveCells) { x += c.x; z += c.z; n++; }
            return n == 0 ? IntVec3.Invalid : new IntVec3((int)(x / n), 0, (int)(z / n));
        }
    }
}
