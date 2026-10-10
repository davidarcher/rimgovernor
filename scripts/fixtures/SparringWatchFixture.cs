using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimGovernor.Runtime;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Stages spectators for the watch-sparring joy giver (#2732) on top of a ring staged
    // by test/sparring_prepare: idle colonists at joy time (a free bystander, a drafted
    // one, one on a work-only schedule), bored of every joy kind but Social so the
    // vanilla joy pick can only choose a Social giver, and a per-tick recorder that
    // reads each watch job the tick it runs (the game runs thousands of ticks between
    // two reads). Test builds only; compiled with SparringFixture.
    public sealed class SparringWatchFixture
    {
        [Tool("test/sparring_watch_prepare", Description = "UNSAFE FOR MODEL EXECUTION. After test/sparring_prepare: stage spectators near the ring (a free bystander at joy time; optionally a drafted one and one whose schedule is all work) and a recorder of every watch job. Test builds only.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z,
            [ToolParameter(Description = "Free bystanders at joy time, all hours Joy, joy low.")] int bystanders = 1,
            [ToolParameter(Description = "Add one drafted bystander.")] bool drafted = false,
            [ToolParameter(Description = "Add one bystander whose timetable is all Work.")] bool working = false,
            [ToolParameter(Description = "Cells north of the ring row the bystanders start.")] int north = 12)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                foreach (var old in map.components.OfType<SparringWatchRecorder>().ToList()) map.components.Remove(old);
                var training = DefDatabase<WorkTypeDef>.GetNamed("RimGovernorTraining");
                var pool = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings.GetPriority(training) == 0).OrderBy(p => p.thingIDNumber).ToList();
                var needed = bystanders + (drafted ? 1 : 0) + (working ? 1 : 0);
                if (pool.Count < needed) throw new InvalidOperationException("Need " + needed + " colonists that do not spar.");
                var boredKinds = DefDatabase<JoyKindDef>.AllDefsListForReading.Where(k => k != JoyKindDefOf.Social).ToList();
                var free = new List<Pawn>();
                var index = 0;
                Pawn Stage(TimeAssignmentDef assignment, bool draft)
                {
                    var p = pool[index];
                    p.drafter.Drafted = false;
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    p.Position = CellFinder.StandableCellNear(new IntVec3(x + 3 + 2 * index, 0, z + north), map, 4f);
                    p.Notify_Teleported(true, true);
                    for (var hour = 0; hour < 24; hour++) p.timetable.SetAssignment(hour, assignment);
                    foreach (var kind in boredKinds) p.needs.joy.tolerances.Notify_JoyGained(1f, kind);
                    p.needs.joy.CurLevel = 0.1f;
                    p.drafter.Drafted = draft;
                    index++;
                    return p;
                }
                for (var i = 0; i < bystanders; i++) free.Add(Stage(TimeAssignmentDefOf.Joy, false));
                var draftedPawn = drafted ? Stage(TimeAssignmentDefOf.Joy, true) : null;
                var worker = working ? Stage(TimeAssignmentDefOf.Work, false) : null;
                var recorder = new SparringWatchRecorder(map, free, draftedPawn, worker);
                map.components.Add(recorder);
                return new { success = true, free = free.Select(p => p.GetUniqueLoadID()).ToArray(), drafted = draftedPawn?.GetUniqueLoadID() ?? "", working = worker?.GetUniqueLoadID() ?? "" };
            }, cancellationToken);
        }

        [Tool("test/sparring_watch_inspect", Description = "Read-only: everything the recorder saw of the staged spectators.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var recorder = map.components.OfType<SparringWatchRecorder>().FirstOrDefault();
                if (recorder == null) return new { success = true, recording = false };
                return new
                {
                    success = true, recording = true, tick = Find.TickManager.TicksGame,
                    fightStart = recorder.FightStartTick, fightOver = recorder.FightOverTick, fightedEver = recorder.FightedEver,
                    watchers = recorder.History.Select(w => (object)new
                    {
                        id = w.Id, free = w.Free, started = w.StartTick, ended = w.EndTick, joyStart = w.JoyStart, joyMax = w.JoyMax,
                        cellOk = w.CellOk, cellNote = w.CellNote, dist = w.Distance, fighter = w.WasFighter, atCellTicks = w.AtCellTicks,
                        fightingAtEnd = w.FightingAtEnd, thought = w.ThoughtAfterEnd, forbiddenOrDoubled = w.Doubled,
                    }).ToArray(),
                    bystanders = recorder.Bystanders.Select(p => (object)new
                    {
                        id = p.GetUniqueLoadID(), job = p.CurJobDef?.defName ?? "", drafted = p.Drafted, joy = p.needs.joy.CurLevel,
                    }).ToArray(),
                    watchJobs = recorder.WatchJobTicks, fighterWatchTicks = recorder.FighterWatchTicks, otherSocial = recorder.OtherSocialInterrupts,
                };
            }, cancellationToken);
        }
    }

    public sealed class SparringWatchRecord
    {
        public string Id;
        public bool Free;
        public int StartTick;
        public int EndTick = -1;
        public float JoyStart;
        public float JoyMax;
        public bool CellOk;
        public string CellNote = "";
        public float Distance;
        public bool WasFighter;
        public int AtCellTicks;
        public bool FightingAtEnd;
        public bool ThoughtAfterEnd;
        public bool Doubled;
        public IntVec3 Cell;
        public Pawn Pawn;
    }

    public sealed class SparringWatchRecorder : MapComponent
    {
        public readonly List<Pawn> Bystanders = new List<Pawn>();
        private readonly HashSet<Pawn> free = new HashSet<Pawn>();
        public readonly Dictionary<int, SparringWatchRecord> Watches = new Dictionary<int, SparringWatchRecord>();
        public readonly List<SparringWatchRecord> History = new List<SparringWatchRecord>();
        public int FightStartTick = -1;
        public int FightOverTick = -1;
        public bool FightedEver;
        public int WatchJobTicks;
        public int FighterWatchTicks;
        public int OtherSocialInterrupts;
        private readonly Pawn draftedPawn;

        public SparringWatchRecorder(Map map, List<Pawn> freePawns, Pawn draftedPawn, Pawn worker) : base(map)
        {
            Bystanders.AddRange(freePawns);
            foreach (var p in freePawns) free.Add(p);
            this.draftedPawn = draftedPawn;
            if (draftedPawn != null) Bystanders.Add(draftedPawn);
            if (worker != null) Bystanders.Add(worker);
        }

        public override void MapComponentTick()
        {
            var now = Find.TickManager.TicksGame;
            var registry = SparringBouts.For(map);
            var fighting = registry.LiveFighting().Any();
            if (fighting && FightStartTick < 0) FightStartTick = now;
            if (fighting) FightedEver = true;
            if (!fighting && FightedEver && FightOverTick < 0) FightOverTick = now;
            // Vanilla auto-undrafts a pawn after a quiet spell; this one stays drafted.
            if (draftedPawn != null && !draftedPawn.Drafted) draftedPawn.drafter.Drafted = true;
            var watch = DefDatabase<JobDef>.GetNamed(SparringWatch.JobName);
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                var watching = p.CurJobDef == watch;
                Watches.TryGetValue(p.thingIDNumber, out var record);
                if (watching)
                {
                    WatchJobTicks++;
                    if (registry.BoutOf(p) != null) FighterWatchTicks++;
                    if (record == null || record.EndTick >= 0)
                    {
                        record = new SparringWatchRecord { Id = p.GetUniqueLoadID(), Free = free.Contains(p), StartTick = now, JoyStart = p.needs.joy.CurLevel, Pawn = p };
                        record.Cell = p.CurJob.targetA.Cell;
                        Check(record, registry, p);
                        Watches[p.thingIDNumber] = record;
                        History.Add(record);
                        p.needs?.mood?.thoughts?.memories?.RemoveMemoriesOfDef(DefDatabase<ThoughtDef>.GetNamed(SparringWatch.ThoughtName));
                    }
                    record.JoyMax = Math.Max(record.JoyMax, p.needs.joy.CurLevel);
                    if (registry.BoutOf(p) != null) record.WasFighter = true;
                    if (p.Position == record.Cell) record.AtCellTicks++;
                    if (p.Position.IsForbidden(p) || p.CurJob.targetA.Cell != record.Cell) record.Doubled = true;
                }
                else if (record != null && record.EndTick < 0)
                {
                    record.EndTick = now;
                    record.FightingAtEnd = fighting;
                }
                else if (record != null && record.EndTick >= 0 && now - record.EndTick == 3)
                {
                    record.ThoughtAfterEnd = p.needs?.mood?.thoughts?.memories?.GetFirstMemoryOfDef(DefDatabase<ThoughtDef>.GetNamed(SparringWatch.ThoughtName)) != null;
                }
                // The vanilla joy pick is random among Social givers: another one (social relax,
                // visiting) is ended so the next pick can be the watch.
                if (!watching && free.Contains(p) && p.CurJobDef?.joyKind == JoyKindDefOf.Social && p.CurJobDef != watch && !FightedEverOver())
                {
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    OtherSocialInterrupts++;
                }
            }
        }

        private bool FightedEverOver() => FightOverTick >= 0;

        // The spectator cell, judged against the bout it was chosen for.
        private void Check(SparringWatchRecord record, SparringBouts registry, Pawn p)
        {
            var bout = registry.LiveFighting().OrderBy(b => b.Id).FirstOrDefault();
            if (bout == null) { record.CellNote = "no fighting bout"; return; }
            var fighters = bout.Slots.Select(s => s.Pawn).ToList();
            record.Distance = fighters.Min(f => (f.Position - record.Cell).LengthHorizontal);
            var interior = SparringWatch.RingInterior(bout);
            var los = fighters.Any(f => GenSight.LineOfSight(record.Cell, f.Position, map));
            record.CellOk = record.Distance >= SparringWatch.MinDistance - 1f && record.Distance <= SparringWatch.MaxDistance + 1f
                && !interior.Contains(record.Cell) && record.Cell.Standable(map) && !record.Cell.IsForbidden(p) && los;
            record.CellNote = (interior.Contains(record.Cell) ? "inside ring;" : "") + (los ? "" : "no line of sight;") + (record.CellOk ? "" : "dist " + record.Distance);
        }
    }
}
