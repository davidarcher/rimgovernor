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
    // Stages a sparring ring for bout formation (#2708) and the spar job (#2709): a
    // row of markers, N eligible colonists far south of it whose only work is
    // training, and optionally two colonists who must never be seated (Melee above
    // every ceiling; drafted). A watcher component samples the bout registry every
    // tick, because the game runs thousands of ticks between two reads and a
    // gathering or fighting state would otherwise pass unseen. It also records the
    // gear each fighter wore, what each spar job left behind the tick it ended, and
    // drives the scenarios that need an event mid-bout (injury, draft, death).
    public sealed class SparringFixture
    {
        [Tool("test/sparring_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage a sparring ring at the cell: markers in a row, eligible colonists whose only work is training, optional never-eligible colonists. Resets earlier staging. Test builds only.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z,
            [ToolParameter(Description = "How many eligible colonists spar.")] int eligible,
            [ToolParameter(Description = "Comma-separated Melee levels for the eligible colonists in order (default 7,6,5,4,3,2).")] string levels = "7,6,5,4,3,2",
            [ToolParameter(Description = "Markers in the ring row.")] int markers = 6,
            [ToolParameter(Description = "Add two colonists who must never be seated: Melee 20, and drafted.")] bool ineligible = false,
            [ToolParameter(Description = "Add one trainee whose chosen skill is Shooting (Shooting 15 over Melee 5): never seated.")] bool shooter = false,
            [ToolParameter(Description = "Draft one member of the first gathering bout, the tick it is seen.")] bool draftOnGather = false,
            [ToolParameter(Description = "Mid-bout event: '' none; 'cap' no pain (painstopper) so only the exchange cap ends a bout; 'pain' or 'bleed' injures the first fighter past the stop rule the tick it wears practice gear; 'draft' or 'kill' hits the second fighter after its second swing.")] string scenario = "",
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
                var needed = eligible + (ineligible ? 2 : 0) + (shooter ? 1 : 0);
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
                var painstopper = DefDatabase<HediffDef>.GetNamed("Painstopper");
                // Quiet every colonist first: ending a spar job makes the pawn look for work
                // at once, which would form bouts from a half-staged ring.
                foreach (var p in capable)
                {
                    p.drafter.Drafted = false;
                    p.workSettings.EnableAndInitialize();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) p.workSettings.SetPriority(work, 0);
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                void Stage(Pawn p, int at, int level, bool trains, bool drafted, int shooting = 0)
                {
                    p.Position = new IntVec3(x + at, 0, z - far);
                    p.Notify_Teleported(true, true);
                    // Earlier stages leave bruises and pain: every stage starts unhurt.
                    foreach (var hediff in p.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == painstopper).ToList()) p.health.RemoveHediff(hediff);
                    p.mindState.meleeThreat = null;
                    p.needs?.mood?.thoughts?.memories?.RemoveMemoriesOfDef(DefDatabase<ThoughtDef>.GetNamed(TrainingCompany.ThoughtName));
                    if (p.equipment.Primary == null)
                    {
                        p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDefOf.MeleeWeapon_Knife, ThingDefOf.Steel));
                    }
                    var record = p.skills.GetSkill(SkillDefOf.Melee);
                    record.Level = level;
                    record.xpSinceLastLevel = 0f;
                    record.xpSinceMidnight = 0f;
                    record.passion = Passion.Minor;
                    // The skill a pawn trains is its higher one: Shooting stays under every
                    // Melee level here unless the stage makes the pawn a shooter.
                    var shot = p.skills.GetSkill(SkillDefOf.Shooting);
                    shot.Level = shooting;
                    shot.xpSinceLastLevel = 0f;
                    shot.xpSinceMidnight = 0f;
                    shot.passion = Passion.None;
                    var brawler = p.story.traits.GetTrait(TraitDefOf.Brawler);
                    if (brawler != null) p.story.traits.RemoveTrait(brawler);
                    if (trains) p.workSettings.SetPriority(training, 1);
                    p.drafter.Drafted = drafted;
                }
                var eligibleIds = new List<string>();
                var tracked = new List<Pawn>();
                for (var i = 0; i < eligible; i++)
                {
                    Stage(capable[i], i, levelList[i % levelList.Count], true, false);
                    if (scenario == "cap" || scenario == "draft" || scenario == "kill") capable[i].health.AddHediff(painstopper, capable[i].health.hediffSet.GetBrain());
                    eligibleIds.Add(capable[i].GetUniqueLoadID());
                    tracked.Add(capable[i]);
                }
                var ineligibleIds = new List<string>();
                if (ineligible)
                {
                    Stage(capable[eligible], eligible, 20, true, false);
                    Stage(capable[eligible + 1], eligible + 1, 5, true, true);
                    ineligibleIds.Add(capable[eligible].GetUniqueLoadID());
                    ineligibleIds.Add(capable[eligible + 1].GetUniqueLoadID());
                }
                var shooterIds = new List<string>();
                if (shooter)
                {
                    var at = eligible + (ineligible ? 2 : 0);
                    Stage(capable[at], at, 5, true, false, 15);
                    shooterIds.Add(capable[at].GetUniqueLoadID());
                }
                for (var i = needed; i < capable.Count; i++) Stage(capable[i], i, 5, false, false);
                foreach (var bout in registry.Bouts.ToList()) foreach (var slot in bout.Slots.ToList()) registry.Leave(slot.Pawn);
                map.components.Add(new SparringWatcher(map, tracked, scenario) { DraftOnGather = draftOnGather });
                return new
                {
                    success = true, eligible = eligibleIds, ineligible = ineligibleIds, shooter = shooterIds, markers = markerIds,
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
                    injected = watcher?.Injected ?? "", hit = watcher?.HitId ?? "",
                    abandoned = watcher?.Abandoned ?? 0, attackJobs = watcher?.AttackJobs ?? 0, threatTicks = watcher?.ThreatTicks ?? 0,
                    struck = watcher == null ? 0 : watcher.Struck.Count,
                    pawns = watcher == null ? new object[0] : watcher.Tracked.Select(p => (object)watcher.Report(p)).ToArray(),
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

    public sealed class SparringGear
    {
        public int WeaponId = -1;
        public HashSet<int> ApparelIds = new HashSet<int>();
        public int StartLevel;
        public bool SwapSeen;
        public bool SwapAtMarker;
        public bool OriginalsHeld;
        public bool Ended;
        public string Stop = "";
        public string Condition = "";
        public int Exchanges;
        public bool Swapped;
        public bool Dead;
        public bool Drafted;
        public bool Restored;
        public bool NoPractice;
        public bool Originals;
        public bool ThreatNull;
        public bool Thought;
        public int EndedTick;
    }

    public sealed class SparringWatcher : MapComponent
    {
        public bool DraftOnGather;
        public string Drafted = "";
        public int PeakActive;
        public int DoubleBooked;
        public string Injected = "";
        public string HitId = "";
        public int Abandoned;
        public int AttackJobs;
        public int ThreatTicks;
        public readonly HashSet<int> Struck = new HashSet<int>();
        public readonly HashSet<string> Seated = new HashSet<string>();
        public readonly Dictionary<int, SparringRecord> Records = new Dictionary<int, SparringRecord>();
        public readonly List<Pawn> Tracked;
        private readonly Dictionary<int, SparringGear> gear = new Dictionary<int, SparringGear>();
        private readonly Dictionary<int, int> injuries = new Dictionary<int, int>();
        private readonly string scenario;
        private readonly int startTick;
        private bool fired;

        public SparringWatcher(Map map, List<Pawn> tracked, string scenario) : base(map)
        {
            Tracked = tracked;
            this.scenario = scenario;
            startTick = Find.TickManager.TicksGame;
            foreach (var p in tracked)
            {
                var g = new SparringGear { StartLevel = p.skills.GetSkill(SkillDefOf.Melee).Level };
                if (p.equipment.Primary != null) g.WeaponId = p.equipment.Primary.thingIDNumber;
                foreach (var a in p.apparel.WornApparel) g.ApparelIds.Add(a.thingIDNumber);
                gear[p.thingIDNumber] = g;
                injuries[p.thingIDNumber] = Injuries(p);
            }
        }

        private static int Injuries(Pawn p) => p.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);

        private static bool IsPractice(ThingDef def) => def.GetModExtension<SparringTier>() != null || def.defName.StartsWith("Apparel_Practice");

        private static bool Carries(Pawn p) =>
            p.equipment.AllEquipmentListForReading.Any(t => IsPractice(t.def)) || p.apparel.WornApparel.Any(t => IsPractice(t.def))
            || p.inventory.innerContainer.Any(t => IsPractice(t.def));

        private static bool WearsPracticeSet(Pawn p) =>
            p.equipment.Primary != null && IsPractice(p.equipment.Primary.def) && p.apparel.WornApparel.Count > 0 && p.apparel.WornApparel.All(a => IsPractice(a.def));

        public object Report(Pawn p)
        {
            var g = gear[p.thingIDNumber];
            var melee = p.skills.GetSkill(SkillDefOf.Melee);
            return new
            {
                id = p.GetUniqueLoadID(), startMelee = g.StartLevel, melee = melee.Level, xp = melee.Level > g.StartLevel || melee.xpSinceLastLevel > 0f,
                swapSeen = g.SwapSeen, swapAtMarker = g.SwapAtMarker, originalsHeld = g.OriginalsHeld,
                ended = g.Ended, stop = g.Stop, condition = g.Condition, exchanges = g.Exchanges, swapped = g.Swapped, dead = g.Dead, drafted = g.Drafted,
                restored = g.Restored, noPractice = g.NoPractice, originals = g.Originals, threatNull = g.ThreatNull, trainedWith = g.Thought, endedTick = g.EndedTick,
            };
        }

        public override void MapComponentTick()
        {
            var registry = SparringBouts.For(map);
            var bouts = registry.Bouts;
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
                    record.FightOpponents = bout.Slots.Select(s => registry.OpponentOf(s.Pawn)?.GetUniqueLoadID() ?? "").ToArray();
                }
                if (bout.State == BoutState.Fighting) Fighting(bout);
            }
            var live = new HashSet<int>(bouts.Select(b => b.Id));
            foreach (var record in Records.Values)
            {
                if (!live.Contains(record.Id)) record.Finished = true;
            }
            var attack = JobDefOf.AttackMelee;
            foreach (var p in Tracked)
            {
                if (p.CurJobDef == attack) AttackJobs++;
                if (p.Dead || !gear.TryGetValue(p.thingIDNumber, out var g)) continue;
                var count = Injuries(p);
                if (count > injuries[p.thingIDNumber]) Struck.Add(p.thingIDNumber);
                injuries[p.thingIDNumber] = count;
            }
            foreach (var p in Tracked) Capture(registry, p);
        }

        private void Fighting(Bout bout)
        {
            var spar = DefDatabase<JobDef>.GetNamed(SparringBouts.JobName);
            foreach (var slot in bout.Slots)
            {
                var p = slot.Pawn;
                if (!gear.TryGetValue(p.thingIDNumber, out var g)) continue;
                if (p.CurJobDef != spar) Abandoned++;
                if (p.mindState.meleeThreat != null) ThreatTicks++;
                if (!g.SwapSeen && WearsPracticeSet(p))
                {
                    g.SwapSeen = true;
                    g.SwapAtMarker = p.Position == slot.Marker.Position;
                    var held = p.inventory.innerContainer.Select(t => t.thingIDNumber).ToHashSet();
                    g.OriginalsHeld = g.ApparelIds.All(held.Contains) && (g.WeaponId < 0 || held.Contains(g.WeaponId));
                }
            }
            if (fired || scenario == "" || scenario == "cap") return;
            var first = bout.Slots[0].Pawn;
            var second = bout.Slots.Count > 1 ? bout.Slots[1].Pawn : null;
            if ((scenario == "pain" || scenario == "bleed") && gear.TryGetValue(first.thingIDNumber, out var firstGear) && firstGear.SwapSeen)
            {
                fired = true;
                Injected = first.GetUniqueLoadID();
                Injure(first, scenario == "bleed");
                injuries[first.thingIDNumber] = Injuries(first);
            }
            else if ((scenario == "draft" || scenario == "kill") && second != null && second.jobs.curDriver is JobDriver_Spar driver && driver.Exchanges >= 2)
            {
                fired = true;
                HitId = second.GetUniqueLoadID();
                if (scenario == "draft") second.drafter.Drafted = true; else second.Kill(null);
            }
        }

        // Adds injuries until the pawn is past the stop rule's limit and no further.
        private static void Injure(Pawn p, bool bleeding)
        {
            var def = DefDatabase<HediffDef>.GetNamed(bleeding ? "Cut" : "Bruise");
            var parts = p.RaceProps.body.AllParts.Where(r => r.def == BodyPartDefOf.Arm || r.def == BodyPartDefOf.Leg).ToList();
            // Cuts hurt too, and the pain limit is checked first: bleeding must be what stops this pawn.
            if (bleeding) p.health.AddHediff(DefDatabase<HediffDef>.GetNamed("Painstopper"), p.health.hediffSet.GetBrain());
            for (var i = 0; i < 60; i++)
            {
                var health = p.health.hediffSet;
                if (bleeding ? health.BleedRateTotal > SparringStopRule.BleedLimit * 1.5f : health.PainTotal > SparringStopRule.PainLimit * 1.2f) return;
                var part = parts[i % parts.Count];
                var hediff = HediffMaker.MakeHediff(def, p, part);
                hediff.Severity = 3f;
                p.health.AddHediff(hediff, part);
            }
        }

        // The first time a pawn's spar job is seen to have ended, what it left.
        private void Capture(SparringBouts registry, Pawn p)
        {
            var g = gear[p.thingIDNumber];
            var session = registry.LastSession(p);
            if (g.Ended || session == null || session.EndedTick <= startTick) return;
            g.Ended = true;
            g.EndedTick = session.EndedTick;
            g.Stop = session.Stop.ToString();
            g.Condition = session.Condition.ToString();
            g.Exchanges = session.Exchanges;
            g.Swapped = session.Swapped;
            g.Dead = p.Dead;
            g.Drafted = p.Drafted;
            g.ThreatNull = p.mindState == null || p.mindState.meleeThreat == null;
            g.Thought = TrainingFixture.HasTrainedWith(p);
            var ground = map.listerThings.AllThings.Any(t => IsPractice(t.def));
            g.NoPractice = !Carries(p) && !ground;
            var all = g.ApparelIds.Select(id => FindThing(p, id)).ToList();
            g.Originals = all.All(t => t != null && !t.Destroyed) && (g.WeaponId < 0 || FindThing(p, g.WeaponId) is Thing w && !w.Destroyed);
            var worn = p.apparel.WornApparel.Select(a => a.thingIDNumber).ToHashSet();
            var weaponBack = g.WeaponId < 0 ? p.equipment.Primary == null : p.equipment.Primary != null && p.equipment.Primary.thingIDNumber == g.WeaponId;
            var held = p.inventory.innerContainer.Select(t => t.thingIDNumber).ToHashSet();
            g.Restored = worn.SetEquals(g.ApparelIds) && !g.ApparelIds.Any(held.Contains) && weaponBack;
        }

        // An original thing, wherever it now is: worn, held, wielded or on the ground.
        private Thing FindThing(Pawn p, int id)
        {
            return p.apparel.WornApparel.Cast<Thing>().Concat(p.equipment.AllEquipmentListForReading).Concat(p.inventory.innerContainer)
                .Concat(map.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == id);
        }
    }
}
