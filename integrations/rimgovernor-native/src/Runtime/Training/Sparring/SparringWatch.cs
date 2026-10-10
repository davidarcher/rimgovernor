#nullable disable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Runtime
{
    // Spectating a sparring bout (#2732). Where a free colonist may stand to watch
    // and what a watch earns; the giver and the driver below are thin shells over it.
    // Reads only the runtime bout registry (SparringBouts): no saved state.
    public static class SparringWatch
    {
        public const string JobName = "RimGovernor_WatchSparring";
        public const string ThoughtName = "RimGovernor_WatchedSparring";

        // A spectator cell is this far (cells) from the nearest fighter.
        public const float MinDistance = 3f;
        public const float MaxDistance = 8f;
        // A watch that ends Succeeded before this many ticks earns no thought.
        public const int MinWatchTicks = 300;
        // Markers within this many cells of a bout's markers belong to its ring.
        private const int RingReach = 10;

        // The bout a spectator would watch: one that is fighting, nearest the pawn.
        public static Bout NearestFighting(Pawn pawn)
        {
            return SparringBouts.For(pawn.Map).LiveFighting()
                .OrderBy(b => Standing(b).Min(p => (p.Position - pawn.Position).LengthHorizontalSquared))
                .ThenBy(b => b.Id).FirstOrDefault();
        }

        public static bool Fighting(Map map) => SparringBouts.For(map).LiveFighting().Any();

        private static List<Pawn> Standing(Bout bout) =>
            bout.Slots.Select(s => s.Pawn).Where(p => p.Spawned && !p.Dead && !p.Downed).ToList();

        // The fighter nearest the cell, across every fighting bout; null when none fights.
        public static Pawn NearestFighter(Map map, IntVec3 from)
        {
            return SparringBouts.For(map).LiveFighting().SelectMany(Standing)
                .OrderBy(p => (p.Position - from).LengthHorizontalSquared).ThenBy(p => p.thingIDNumber).FirstOrDefault();
        }

        // The ring's interior, as the box around the markers of the bout's ring. The
        // markers sit on the interior's inner edge, so the box is the interior.
        public static CellRect RingInterior(Bout bout)
        {
            var map = bout.Slots[0].Marker.Map;
            var own = bout.Slots.Select(s => s.Marker.Position).ToList();
            var markers = map.listerThings.ThingsOfDef(SparringBouts.MarkerDef).Where(m => own.Any(c => c.DistanceToSquared(m.Position) <= RingReach * RingReach));
            var rect = CellRect.Empty;
            var first = true;
            foreach (var marker in markers)
            {
                rect = first ? CellRect.SingleCell(marker.Position) : rect.Encapsulate(marker.Position);
                first = false;
            }
            return rect;
        }

        // A free standable cell outside the ring interior, MinDistance to MaxDistance
        // from the nearest fighter, in line of sight of a fighter, not forbidden,
        // reachable and not reserved by another spectator; the one nearest the pawn.
        public static IntVec3 FindCell(Pawn pawn, Bout bout)
        {
            var map = pawn.Map;
            var fighters = Standing(bout);
            if (fighters.Count == 0) return IntVec3.Invalid;
            var interior = RingInterior(bout);
            var seen = new HashSet<IntVec3>();
            var best = IntVec3.Invalid;
            var bestScore = int.MaxValue;
            foreach (var fighter in fighters)
            {
                foreach (var cell in GenRadial.RadialCellsAround(fighter.Position, (int)MaxDistance + 1, true))
                {
                    if (!seen.Add(cell) || !cell.InBounds(map) || !cell.Standable(map) || interior.Contains(cell)) continue;
                    var near = fighters.Min(f => (f.Position - cell).LengthHorizontal);
                    if (near < MinDistance || near > MaxDistance) continue;
                    var score = (cell - pawn.Position).LengthHorizontalSquared;
                    if (score > bestScore || score == bestScore && !Before(cell, best)) continue;
                    if (cell.IsForbidden(pawn) || cell.GetFirstPawn(map) != null) continue;
                    if (!fighters.Any(f => GenSight.LineOfSight(cell, f.Position, map))) continue;
                    if (!pawn.CanReserveAndReach(cell, PathEndMode.OnCell, Danger.None)) continue;
                    best = cell;
                    bestScore = score;
                }
            }
            return best;
        }

        private static bool Before(IntVec3 a, IntVec3 b) => !b.IsValid || (a.x != b.x ? a.x < b.x : a.z < b.z);

        // A watch that ended Succeeded after enough ticks earns the memory.
        public static void Finished(Pawn pawn, JobCondition condition, int watchedTicks)
        {
            if (condition != JobCondition.Succeeded || watchedTicks < MinWatchTicks) return;
            var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(ThoughtName);
            if (def != null) pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(def);
        }
    }

    // Offers a watch when a bout is fighting. Vanilla's joy-time logic (JobGiver_GetJoy)
    // decides who is free and when; this adds no schedule or priority logic. Never a
    // pawn that is in a bout.
    public sealed class JoyGiver_WatchSparring : JoyGiver
    {
        public override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.Map == null || SparringBouts.MarkerDef == null || pawn.Drafted) return null;
            var bouts = SparringBouts.For(pawn.Map);
            if (bouts.BoutOf(pawn) != null) return null;
            var bout = SparringWatch.NearestFighting(pawn);
            if (bout == null) return null;
            var cell = SparringWatch.FindCell(pawn, bout);
            return cell.IsValid ? JobMaker.MakeJob(def.jobDef, cell) : null;
        }
    }

    // Walk to the spectator cell (TargetA), then watch: joy through the vanilla joy
    // path (tolerance included), facing the nearest fighter, until no bout is
    // fighting, joy is full or the job def's joyDuration passes.
    public sealed class JobDriver_WatchSparring : JobDriver
    {
        private int watched;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFinishAction(condition => SparringWatch.Finished(pawn, condition, watched));
            // On the walk only: a bout that ends while watching is a good end (Succeeded), not a failure.
            var go = Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            go.FailOn(() => !SparringWatch.Fighting(pawn.Map) || SparringBouts.For(pawn.Map).BoutOf(pawn) != null);
            yield return go;
            var watch = ToilMaker.MakeToil("WatchSparring");
            watch.defaultCompleteMode = ToilCompleteMode.Never;
            watch.handlingFacing = true;
            watch.AddPreTickIntervalAction(delegate (int delta)
            {
                var fighter = SparringWatch.NearestFighter(pawn.Map, pawn.Position);
                if (fighter == null)
                {
                    EndJobWith(JobCondition.Succeeded);
                    return;
                }
                pawn.rotationTracker.FaceCell(fighter.Position);
                watched += delta;
                pawn.GainComfortFromCellIfPossible(delta);
                if (JoyUtility.JoyTickCheckEnd(pawn, delta, JoyTickFullJoyAction.EndJob, 1f, null)) return;
                if (watched >= job.def.joyDuration) EndJobWith(JobCondition.Succeeded);
            });
            yield return watch;
        }
    }
}
