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
using Fx = RimGovernor.Fixtures.Contracts;

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
            [ToolParameter(Description = "Mid-bout event: '' none; 'cap' no pain (painstopper) so only the exchange cap ends a bout; 'pain' or 'bleed' injures the first fighter past the stop rule the tick it wears practice gear; 'draft' or 'kill' hits the second fighter after its second swing; 'raid' spawns a hostile human beside it then.")] string scenario = "",
            [ToolParameter(Description = "Cells south of the ring the colonists start.")] int far = 30)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var registry = SparringBouts.For(map);
                foreach (var bout in registry.Bouts.ToList()) foreach (var slot in bout.Slots.ToList()) registry.Leave(slot.Pawn);
                // The colonists the last stage left free form a bout of their own once it is over, and one
                // that nobody reaches puts its members on a no-show cooldown that would carry into this stage.
                SparringWatcher.SitOut(map).Clear();
                foreach (var old in map.components.OfType<SparringWatcher>().ToList()) map.components.Remove(old);
                foreach (var old in map.listerThings.ThingsOfDef(SparringBouts.MarkerDef).ToList()) old.Destroy();

                var capable = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)
                    && !p.skills.GetSkill(SkillDefOf.Melee).TotallyDisabled).OrderBy(p => p.thingIDNumber).ToList();
                var needed = eligible + (ineligible ? 2 : 0) + (shooter ? 1 : 0);
                if (capable.Count < needed) throw new InvalidOperationException("Need " + needed + " violence-capable colonists.");
                var levelList = levels.Split(',').Select(int.Parse).ToList();

                var markerDef = SparringBouts.MarkerDef;
                for (var i = 0; i < markers; i++)
                {
                    var thing = ThingMaker.MakeThing(markerDef, ThingDefOf.WoodLog);
                    thing.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, new IntVec3(x + 2 * i, 0, z), map);
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
                    // The job is not ended here: a pawn that ends its job looks for work at once, and
                    // with the ring half staged it would pick a long joy job (Skygaze) that the
                    // bout's invite window then outlasts. Every job ends together once all is staged.
                    p.Notify_Teleported(false, true);
                    // Earlier stages leave bruises and pain: every stage starts unhurt.
                    foreach (var hediff in p.health.hediffSet.hediffs.Where(h => SparringWatcher.IsHurt(h) || h.def == painstopper).ToList()) p.health.RemoveHediff(hediff);
                    if (p.Dead) ResurrectionUtility.TryResurrect(p);
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
                    // A colonist under 35% Joy (95% in joy hours) after the first 5000 ticks does a joy job
                    // before any work (ThinkNode_Priority_GetJoy), and one that started Skygaze never
                    // reaches the marker inside the invite window. A bout leaves Joy low, so every stage refills it.
                    if (p.needs?.joy != null) p.needs.joy.CurLevel = 1f;
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
                if (ineligible)
                {
                    Stage(capable[eligible], eligible, 20, true, false);
                    Stage(capable[eligible + 1], eligible + 1, 5, true, true);
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
                // Now every colonist is staged: each pawn looks for work as its job ends, and the
                // first to ask forms the bouts from all of the free, eligible ones.
                foreach (var p in capable.Where(c => !c.Drafted)) p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                return FixtureKit.Reply(new Fx.SparringPrepared { Eligible = eligibleIds, Shooter = shooterIds });
            }, cancellationToken);
        }

        [Tool("test/sparring_inspect", Description = "Read-only: the live bout registry and everything the watcher saw of the staged ring.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var watcher = map.components.OfType<SparringWatcher>().FirstOrDefault();
                var active = SparringBouts.For(map).Bouts.Count;
                if (watcher == null) return FixtureKit.Reply(new Fx.SparringRead { Active = active });
                return FixtureKit.Reply(new Fx.SparringRead
                {
                    Active = active, PeakActive = watcher.PeakActive, DoubleBooked = watcher.DoubleBooked, Seated = watcher.Seated.ToList(), Drafted = watcher.Drafted,
                    Bouts = watcher.Records.Values.OrderBy(r => r.Id).Select(r => new Fx.SparringBout
                    {
                        Id = r.Id, Finished = r.Finished,
                        Fighting = r.Fighting,
                    }).ToList(),
                    Injected = watcher.Injected, Hit = watcher.HitId, Abandoned = watcher.Abandoned, AttackJobs = watcher.AttackJobs, Struck = watcher.Struck.Count,
                    Raid = watcher.RaidReport(), Pawns = watcher.Tracked.Select(watcher.Report).ToList(),
                });
            }, cancellationToken);
        }
    }

    public sealed class SparringRecord
    {
        public int Id;
        public bool Finished;
        public Fx.SparringFight? Fighting;
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
        public int Exchanges;
        public bool Swapped;
        public bool Dead;
        public bool Drafted;
        public bool Restored;
        // Each change of the pawn's job (tick, job, whether it held a bout place): why a pawn never reached its marker.
        public readonly List<string> Trail = new List<string>();
        public string LastJob = "";
        public string GearDiff = "";
        public bool NoPractice;
        public bool Originals;
        public bool ThreatNull;
        public bool Thought;
        public float JoyStart = -1f;
        public float JoyEnd = -1f;
        public float ToleranceStart;
        public float ToleranceEnd;
    }

    public sealed class SparringWatcher : MapComponent
    {
        private Pawn? raider;
        private Pawn? raidVictim;
        private int raidSpawnTick;
        private int raidVictimEndTick = -1;
        public bool DraftOnGather;
        public string Drafted = "";
        public int PeakActive;
        public int DoubleBooked;
        public string Injected = "";
        public string HitId = "";
        public int Abandoned;
        public int AttackJobs;
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

        // Anything a bout can leave on a pawn, which every stage wipes.
        public static bool IsHurt(Hediff h) =>
            h is Hediff_Injury || h is Hediff_MissingPart || h.def.defName == "BloodLoss" || h.def.defName == "WoundInfection";

        private static int Injuries(Pawn p) => p.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);

        private static bool IsPractice(ThingDef def) => def.GetModExtension<SparringTier>() != null || def.defName.StartsWith("Apparel_Practice");

        private static bool Carries(Pawn p) =>
            p.equipment.AllEquipmentListForReading.Any(t => IsPractice(t.def)) || p.apparel.WornApparel.Any(t => IsPractice(t.def))
            || p.inventory.innerContainer.Any(t => IsPractice(t.def));

        private static bool WearsPracticeSet(Pawn p) =>
            p.equipment.Primary != null && IsPractice(p.equipment.Primary.def) && p.apparel.WornApparel.Count > 0 && p.apparel.WornApparel.All(a => IsPractice(a.def));

        // The registry's no-show cooldowns (pawn id -> tick until which it sits out): private to the
        // registry, read here because a cooldown left by one stage must not outlast it.
        public static Dictionary<int, int> SitOut(Map map) =>
            ((BridgeCommon.PrivateInstanceField(typeof(SparringBouts), "sitOutUntil") ?? throw new MissingFieldException(typeof(SparringBouts).FullName, "sitOutUntil"))
                .GetValue(SparringBouts.For(map)) as Dictionary<int, int> ?? throw new InvalidOperationException("Sparring sit-out state unavailable."));

        public Fx.SparringPawn Report(Pawn p)
        {
            var g = gear[p.thingIDNumber];
            var melee = p.skills.GetSkill(SkillDefOf.Melee);
            return new Fx.SparringPawn
            {
                Id = p.GetUniqueLoadID(), Ended = g.Ended, Stop = g.Stop, Exchanges = g.Exchanges, Swapped = g.Swapped, SwapSeen = g.SwapSeen, SwapAtMarker = g.SwapAtMarker,
                OriginalsHeld = g.OriginalsHeld, Dead = g.Dead, Drafted = g.Drafted, Restored = g.Restored, NoPractice = g.NoPractice, Originals = g.Originals,
                ThreatNull = g.ThreatNull, Xp = melee.Level > g.StartLevel || melee.xpSinceLastLevel > 0f, TrainedWith = g.Thought,
                JoyStart = g.JoyStart, JoyEnd = g.JoyEnd, ToleranceStart = g.ToleranceStart, ToleranceEnd = g.ToleranceEnd,
                Trail = string.Join(" ", g.Trail), GearDiff = g.GearDiff,
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
                    record = new SparringRecord { Id = bout.Id };
                    Records[bout.Id] = record;
                    if (DraftOnGather && Drafted == "" && bout.State == BoutState.Gathering)
                    {
                        var victim = bout.Slots[0].Pawn;
                        Drafted = victim.GetUniqueLoadID();
                        victim.drafter.Drafted = true;
                    }
                }
                if (bout.State == BoutState.Fighting && record.Fighting == null)
                {
                    record.Fighting = new Fx.SparringFight
                    {
                        Members = bout.Slots.Select(s => s.Pawn.GetUniqueLoadID()).ToArray(),
                        Teams = bout.Slots.Select(s => s.Team).ToArray(),
                        Melee = bout.Slots.Select(s => SparringBouts.Melee(s.Pawn)).ToArray(),
                        Markers = bout.Slots.Select(s => s.Marker.GetUniqueLoadID()).ToArray(),
                        Opponents = bout.Slots.Select(s => registry.OpponentOf(s.Pawn)?.GetUniqueLoadID() ?? "").ToArray(),
                    };
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
            foreach (var p in Tracked)
            {
                var trail = gear[p.thingIDNumber];
                var job = (p.CurJobDef?.defName ?? "none") + (registry.BoutOf(p) != null ? "+bout" : "");
                if (job != trail.LastJob && trail.Trail.Count < 40)
                {
                    trail.LastJob = job;
                    trail.Trail.Add((Find.TickManager.TicksGame - startTick) + ":" + job);
                }
                Capture(registry, p);
            }
            if (scenario == "raid") RaidTick(registry);
        }

        // The raid scenario (#2711): after the second fighter's second swing a hostile
        // human appears beside it and attacks with a real weapon. The raider is removed
        // 240 ticks after the pawn's spar job has ended.
        private void RaidTick(SparringBouts registry)
        {
            var now = Find.TickManager.TicksGame;
            if (raider != null && !raider.Destroyed)
            {
                if (!raider.Dead && raidVictim != null && !raidVictim.Dead && raider.CurJob == null) raider.jobs.StartJob(JobMaker.MakeJob(JobDefOf.AttackMelee, raidVictim), JobCondition.InterruptForced);
                var ended = registry.LastSession(raidVictim)?.EndedTick ?? -1;
                if (raidVictimEndTick < 0 && ended > raidSpawnTick) raidVictimEndTick = now;
                if (raidVictimEndTick >= 0 && now - raidVictimEndTick >= 240 || now - raidSpawnTick > 6000) raider.Destroy();
            }
        }

        public Fx.SparringRaid? RaidReport() => raidVictim == null ? null : new Fx.SparringRaid
        {
            Victim = raidVictim.GetUniqueLoadID(), RaiderGone = raider == null || raider.Destroyed,
        };

        private void SpawnRaider(Pawn victim)
        {
            var faction = Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer && !f.def.hidden && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer))
                .OrderBy(f => f.def == FactionDefOf.Pirate ? 0 : 1).ThenBy(f => f.def.defName).First();
            var kind = DefDatabase<PawnKindDef>.GetNamed("Pirate");
            raider = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, faction, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                mustBeCapableOfViolence: true, allowAddictions: false, developmentalStages: DevelopmentalStage.Adult));
            var cell = CellFinder.StandableCellNear(victim.Position + IntVec3.East, map, 4f) ;
            GenSpawn.Spawn(raider, cell, map);
            raidVictim = victim;
            raidSpawnTick = Find.TickManager.TicksGame;
            raider.jobs.StartJob(JobMaker.MakeJob(JobDefOf.AttackMelee, victim), JobCondition.InterruptForced);
        }

        private void Fighting(Bout bout)
        {
            var spar = DefDatabase<JobDef>.GetNamed(SparringBouts.JobName);
            foreach (var slot in bout.Slots)
            {
                var p = slot.Pawn;
                if (!gear.TryGetValue(p.thingIDNumber, out var g)) continue;
                if (p.CurJobDef != spar) Abandoned++;
                if (!g.SwapSeen && WearsPracticeSet(p))
                {
                    g.SwapSeen = true;
                    g.SwapAtMarker = p.Position == slot.Marker.Position;
                    // Joy (#2731) is set as the fight begins: set earlier, the idle colonist fills it with
                    // vanilla joy jobs before it ever spars. Low, so the bout's gain shows; the cap
                    // scenario's second fighter starts full, which must not end its job.
                    if (p.needs?.joy != null) p.needs.joy.CurLevel = scenario == "cap" && Tracked.IndexOf(p) == 1 ? 1f : 0.1f;
                    g.JoyStart = JoyOf(p);
                    g.ToleranceStart = SocialTolerance(p);
                    var held = p.inventory.innerContainer.Select(t => t.thingIDNumber).ToHashSet();
                    g.OriginalsHeld = g.ApparelIds.All(held.Contains) && (g.WeaponId < 0 || held.Contains(g.WeaponId));
                }
            }
            if (fired || scenario == "" || scenario == "cap") return;
            if (scenario == "raid")
            {
                var target = bout.Slots.Count > 1 ? bout.Slots[1].Pawn : null;
                if (target != null && target.jobs.curDriver is JobDriver_Spar targetDriver && targetDriver.Exchanges >= 2)
                {
                    fired = true;
                    HitId = target.GetUniqueLoadID();
                    SpawnRaider(target);
                }
                return;
            }
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
            g.Stop = session.Stop.ToString();
            g.Exchanges = session.Exchanges;
            g.Swapped = session.Swapped;
            g.Dead = p.Dead;
            g.Drafted = p.Drafted;
            g.ThreatNull = p.mindState == null || p.mindState.meleeThreat == null;
            g.Thought = TrainingFixture.HasTrainedWith(p);
            g.JoyEnd = JoyOf(p);
            g.ToleranceEnd = SocialTolerance(p);
            var ground = map.listerThings.AllThings.Any(t => IsPractice(t.def));
            g.NoPractice = !Carries(p) && !ground;
            var all = g.ApparelIds.Select(id => FindThing(p, id)).ToList();
            g.Originals = all.All(t => t != null && !t.Destroyed) && (g.WeaponId < 0 || FindThing(p, g.WeaponId) is Thing w && !w.Destroyed);
            var worn = p.apparel.WornApparel.Select(a => a.thingIDNumber).ToHashSet();
            var weaponBack = g.WeaponId < 0 ? p.equipment.Primary == null : p.equipment.Primary != null && p.equipment.Primary.thingIDNumber == g.WeaponId;
            var held = p.inventory.innerContainer.Select(t => t.thingIDNumber).ToHashSet();
            g.Restored = worn.SetEquals(g.ApparelIds) && !g.ApparelIds.Any(held.Contains) && weaponBack;
            if (!g.Restored)
            {
                g.GearDiff = "wornNow=" + string.Join(",", p.apparel.WornApparel.Select(a => a.def.defName + "#" + a.thingIDNumber)) + " want=" + string.Join(",", g.ApparelIds)
                    + " held=" + string.Join(",", p.inventory.innerContainer.Select(t => t.def.defName + "#" + t.thingIDNumber))
                    + " weaponNow=" + (p.equipment.Primary?.thingIDNumber ?? -1) + " want=" + g.WeaponId;
            }
        }

        // Joy while fighting (#2731): the Joy level and the Social tolerance, -1 and 0 without a need.
        private static float JoyOf(Pawn p) => p.needs?.joy?.CurLevel ?? -1f;

        private static float SocialTolerance(Pawn p) => p.needs?.joy == null ? 0f : p.needs.joy.tolerances[JoyKindDefOf.Social];

        // An original thing, wherever it now is: worn, held, wielded or on the ground.
        private Thing? FindThing(Pawn p, int id)
        {
            return p.apparel.WornApparel.Cast<Thing>().Concat(p.equipment.AllEquipmentListForReading).Concat(p.inventory.innerContainer)
                .Concat(map.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == id);
        }
    }
}
