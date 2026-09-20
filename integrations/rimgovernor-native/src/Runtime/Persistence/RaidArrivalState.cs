using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Observed hostile arrivals (#581): every SampleInterval ticks each lord
    // of a faction hostile to the player is looked up; the first time one is
    // seen, the position of its first spawned pawn is its spawn cell (ground
    // when that cell lies on the map edge, which is how walk-in raids enter;
    // drop pods and tunnellers land anywhere else). Afterwards the pawn of
    // the lord nearest the colony adds one trail sample, so the controller
    // can find where the raid crossed the boundary of its own census rather
    // than guess from a far map-edge coordinate. Tracks live for the loaded
    // session only and are bounded per lord and in number.
    public sealed class RaidArrivalState : MapComponent
    {
        public const int SampleInterval = 60;
        public const int TrailLimit = 128;
        public const int TrackLimit = 32;

        public sealed class Track
        {
            public int LordId;
            public string FactionDef = "";
            public int SpawnTick;
            public IntVec3 Spawn;
            public bool Ground;
            public int LastTick;
            public readonly List<IntVec3> Trail = new List<IntVec3>();
        }

        public readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>();

        public RaidArrivalState(Map map) : base(map) { }

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
                if (!Tracks.TryGetValue(lord.loadID, out var track))
                {
                    if (Tracks.Count >= TrackLimit) continue;
                    var first = pawns[0].Position;
                    track = new Track { LordId = lord.loadID, FactionDef = lord.faction.def.defName, SpawnTick = tick, Spawn = first, Ground = OnEdge(first) };
                    Tracks[lord.loadID] = track;
                }
                var nearest = pawns.OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber).First().Position;
                track.LastTick = tick;
                if (track.Trail.Count < TrailLimit && (track.Trail.Count == 0 || track.Trail[track.Trail.Count - 1] != nearest)) track.Trail.Add(nearest);
            }
        }

        private bool OnEdge(IntVec3 c) => c.x <= 1 || c.z <= 1 || c.x >= map.Size.x - 2 || c.z >= map.Size.z - 2;

        private static IntVec3 Centroid(Area area)
        {
            long x = 0, z = 0, n = 0;
            foreach (var c in area.ActiveCells) { x += c.x; z += c.z; n++; }
            return n == 0 ? IntVec3.Invalid : new IntVec3((int)(x / n), 0, (int)(z / n));
        }
    }
}
