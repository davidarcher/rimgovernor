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
    // Stages a sparring ring for bout formation (#2708): a row of markers, N
    // eligible colonists far south of it whose only work is training, and
    // optionally two colonists who must never be seated (Melee above every
    // ceiling; drafted). A watcher component samples the bout registry every
    // tick, because the game runs thousands of ticks between two reads and a
    // gathering or fighting state would otherwise pass unseen.
    public sealed class SparringFixture
    {
        [Tool("test/sparring_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage a sparring ring at the cell: markers in a row, eligible colonists whose only work is training, optional never-eligible colonists. Resets earlier staging. Test builds only.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z,
            [ToolParameter(Description = "How many eligible colonists spar.")] int eligible,
            [ToolParameter(Description = "Comma-separated Melee levels for the eligible colonists in order (default 7,6,5,4,3,2).")] string levels = "7,6,5,4,3,2",
            [ToolParameter(Description = "Markers in the ring row.")] int markers = 6,
            [ToolParameter(Description = "Add two colonists who must never be seated: Melee 20, and drafted.")] bool ineligible = false,
            [ToolParameter(Description = "Draft one member of the first gathering bout, the tick it is seen.")] bool draftOnGather = false,
            [ToolParameter(Description = "Cells south of the ring the colonists start.")] int far = 30)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var registry = SparringBouts.For(map);
                foreach (var bout in registry.Bouts.ToList()) foreach (var slot in bout.Slots.ToList()) registry.Leave(slot.Pawn);
                foreach (var old in map.components.OfType<SparringWatcher>().ToList()) map.components.Remove(old);
                foreach (var old in map.listerThings.ThingsOfDef(SparringBouts.MarkerDef).ToList()) old.Destroy();

                var capable = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)
                    && !p.skills.GetSkill(SkillDefOf.Melee).TotallyDisabled).OrderBy(p => p.thingIDNumber).ToList();
                var needed = eligible + (ineligible ? 2 : 0);
                if (capable.Count < needed) throw new InvalidOperationException("Need " + needed + " violence-capable colonists.");
                var levelList = levels.Split(',').Select(int.Parse).ToList();

                var markerDef = SparringBouts.MarkerDef;
                var markerIds = new List<string>();
                for (var i = 0; i < markers; i++)
                {
                    var thing = ThingMaker.MakeThing(markerDef, ThingDefOf.WoodLog);
                    thing.SetFaction(Faction.OfPlayer);
                    markerIds.Add(GenSpawn.Spawn(thing, new IntVec3(x + 2 * i, 0, z), map).GetUniqueLoadID());
                }

                var training = DefDatabase<WorkTypeDef>.GetNamed("RimGovernorTraining");
                // Quiet every colonist first: ending a spar job makes the pawn look for work
                // at once, which would form bouts from a half-staged ring.
                foreach (var p in capable)
                {
                    p.drafter.Drafted = false;
                    p.workSettings.EnableAndInitialize();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) p.workSettings.SetPriority(work, 0);
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                void Stage(Pawn p, int at, int level, bool trains, bool drafted)
                {
                    p.Position = new IntVec3(x + at, 0, z - far);
                    p.Notify_Teleported(true, true);
                    var record = p.skills.GetSkill(SkillDefOf.Melee);
                    record.Level = level;
                    record.xpSinceLastLevel = 0f;
                    record.xpSinceMidnight = 0f;
                    record.passion = Passion.Minor;
                    if (trains) p.workSettings.SetPriority(training, 1);
                    p.drafter.Drafted = drafted;
                }
                var eligibleIds = new List<string>();
                for (var i = 0; i < eligible; i++)
                {
                    Stage(capable[i], i, levelList[i % levelList.Count], true, false);
                    eligibleIds.Add(capable[i].GetUniqueLoadID());
                }
                var ineligibleIds = new List<string>();
                if (ineligible)
                {
                    Stage(capable[eligible], eligible, 20, true, false);
                    Stage(capable[eligible + 1], eligible + 1, 5, true, true);
                    ineligibleIds.Add(capable[eligible].GetUniqueLoadID());
                    ineligibleIds.Add(capable[eligible + 1].GetUniqueLoadID());
                }
                for (var i = needed; i < capable.Count; i++) Stage(capable[i], i, 5, false, false);
                foreach (var bout in registry.Bouts.ToList()) foreach (var slot in bout.Slots.ToList()) registry.Leave(slot.Pawn);
                map.components.Add(new SparringWatcher(map) { DraftOnGather = draftOnGather });
                return new
                {
                    success = true, eligible = eligibleIds, ineligible = ineligibleIds, markers = markerIds,
                    ceiling = SparringRules.Ceiling,
                };
            }, cancellationToken);
        }

        [Tool("test/sparring_inspect", Description = "Read-only: the live bout registry and everything the watcher saw of the staged ring.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var watcher = map.components.OfType<SparringWatcher>().FirstOrDefault();
                return new
                {
                    success = true, tick = Find.TickManager.TicksGame, active = SparringBouts.For(map).Bouts.Count,
                    watching = watcher != null,
                    peakActive = watcher?.PeakActive ?? 0, doubleBooked = watcher?.DoubleBooked ?? 0,
                    seated = watcher == null ? new string[0] : watcher.Seated.ToArray(),
                    drafted = watcher?.Drafted ?? "", 
                    bouts = watcher == null ? new object[0] : watcher.Records.Values.OrderBy(r => r.Id).Select(r => (object)new
                    {
                        id = r.Id, formed = r.Formed, finished = r.Finished,
                        fighting = r.FightMembers == null ? null : new { members = r.FightMembers, teams = r.FightTeams, melee = r.FightMelee, opponents = r.FightOpponents, markers = r.FightMarkers },
                    }).ToArray(),
                };
            }, cancellationToken);
        }
    }

    public sealed class SparringRecord
    {
        public int Id;
        public string[] Formed;
        public bool Finished;
        public string[] FightMembers;
        public int[] FightTeams;
        public int[] FightMelee;
        public string[] FightOpponents;
        public string[] FightMarkers;
    }

    public sealed class SparringWatcher : MapComponent
    {
        public bool DraftOnGather;
        public string Drafted = "";
        public int PeakActive;
        public int DoubleBooked;
        public readonly HashSet<string> Seated = new HashSet<string>();
        public readonly Dictionary<int, SparringRecord> Records = new Dictionary<int, SparringRecord>();

        public SparringWatcher(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            var bouts = SparringBouts.For(map).Bouts;
            PeakActive = Math.Max(PeakActive, bouts.Count);
            var pawns = new HashSet<Pawn>();
            var markers = new HashSet<Thing>();
            foreach (var bout in bouts)
            {
                foreach (var slot in bout.Slots)
                {
                    if (!pawns.Add(slot.Pawn)) DoubleBooked++;
                    if (!markers.Add(slot.Marker)) DoubleBooked++;
                    Seated.Add(slot.Pawn.GetUniqueLoadID());
                }
                if (!Records.TryGetValue(bout.Id, out var record))
                {
                    record = new SparringRecord { Id = bout.Id, Formed = bout.Slots.Select(s => s.Pawn.GetUniqueLoadID()).ToArray() };
                    Records[bout.Id] = record;
                    if (DraftOnGather && Drafted == "" && bout.State == BoutState.Gathering)
                    {
                        var victim = bout.Slots[0].Pawn;
                        Drafted = victim.GetUniqueLoadID();
                        victim.drafter.Drafted = true;
                    }
                }
                if (bout.State == BoutState.Fighting && record.FightMembers == null)
                {
                    record.FightMembers = bout.Slots.Select(s => s.Pawn.GetUniqueLoadID()).ToArray();
                    record.FightTeams = bout.Slots.Select(s => s.Team).ToArray();
                    record.FightMelee = bout.Slots.Select(s => SparringBouts.Melee(s.Pawn)).ToArray();
                    record.FightMarkers = bout.Slots.Select(s => s.Marker.GetUniqueLoadID()).ToArray();
                    record.FightOpponents = bout.Slots.Select(s => SparringBouts.For(map).OpponentOf(s.Pawn)?.GetUniqueLoadID() ?? "").ToArray();
                }
            }
            var live = new HashSet<int>(bouts.Select(b => b.Id));
            foreach (var record in Records.Values)
            {
                if (!live.Contains(record.Id)) record.Finished = true;
            }
        }
    }
}
