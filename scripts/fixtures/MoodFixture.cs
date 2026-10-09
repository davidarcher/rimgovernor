using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse.AI.Group;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Test-build-only setup; subsequent recovery must use ordinary native ticks.
    public sealed class MoodFixture
    {
        [Tool("test/mental_state_berserk", Description = "UNSAFE FOR MODEL EXECUTION. Induce Berserk in one paused disposable colonist for mental-state readback.")]
        public async Task<object> Berserk(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && !p.InMentalState);
                pawn.drafter.Drafted = false;
                if (!pawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, forceWake: true)
                    || pawn.MentalStateDef != MentalStateDefOf.Berserk)
                    throw new InvalidOperationException("Berserk did not start.");
                return new { success = true, pawn = pawn.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/inspire_creativity", Description = "UNSAFE FOR MODEL EXECUTION. Give one paused disposable colonist Inspired_Creativity for inspiration readback.")]
        public async Task<object> InspireCreativity(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var def = DefDatabase<InspirationDef>.GetNamed("Inspired_Creativity");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && !p.InMentalState && p.mindState?.inspirationHandler != null && !p.Inspired);
                if (!pawn.mindState.inspirationHandler.TryStartInspiration(def) || pawn.InspirationDef != def)
                    throw new InvalidOperationException("Inspired_Creativity did not start.");
                return new { success = true, pawn = pawn.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/pacifist_ignore", Description = "UNSAFE FOR MODEL EXECUTION. Give one paused disposable colonist an adulthood backstory that disables violence and set its hostility response to Ignore (#1299).")]
        public async Task<object> PacifistIgnore(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && p.story != null && p.playerSettings != null);
                var backstory = DefDatabase<BackstoryDef>.AllDefsListForReading
                    .Where(b => b.slot == BackstorySlot.Adulthood && (b.workDisables & WorkTags.Violent) != 0)
                    .OrderBy(b => b.workDisables.ToString().Length).ThenBy(b => b.defName).First();
                pawn.story.Adulthood = backstory;
                pawn.Notify_DisabledWorkTypesChanged();
                pawn.equipment?.DestroyAllEquipment();
                pawn.playerSettings.hostilityResponse = HostilityResponseMode.Ignore;
                if (!pawn.WorkTagIsDisabled(WorkTags.Violent))
                    throw new InvalidOperationException("Violence is still enabled.");
                return new { success = true, pawn = pawn.GetUniqueLoadID(), backstory = backstory.defName };
            }, cancellationToken);
        }
        [Tool("test/lone_self_tend_off", Description = "UNSAFE FOR MODEL EXECUTION. Vanish every free colonist but one paused disposable doctor-capable colonist and turn its self-tend off (#1305).")]
        public async Task<object> LoneSelfTendOff(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && p.playerSettings != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                foreach (var other in map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn).ToList())
                    other.Destroy(DestroyMode.Vanish);
                pawn.playerSettings.selfTend = false;
                if (map.mapPawns.FreeColonistsSpawned.Count != 1)
                    throw new InvalidOperationException("More than one colonist remains.");
                return new { success = true, pawn = pawn.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/reader_and_child", Description = "UNSAFE FOR MODEL EXECUTION. Keep two paused disposable colonists: a researcher (manual priorities, Research 1) and a child aged eight (#1306).")]
        public async Task<object> ReaderAndChild(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var research = DefDatabase<WorkTypeDef>.GetNamed("Research");
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.workSettings != null && p.reading != null)
                    .OrderBy(p => p.thingIDNumber).ToList();
                var researcher = pawns.First(p => !p.WorkTypeIsDisabled(research));
                var child = pawns.First(p => p != researcher);
                foreach (var other in map.mapPawns.FreeColonistsSpawned.Where(p => p != researcher && p != child).ToList())
                    other.Destroy(DestroyMode.Vanish);
                researcher.workSettings.EnableAndInitialize();
                researcher.workSettings.SetPriority(research, 1);
                child.ageTracker.AgeBiologicalTicks = 8L * GenDate.TicksPerYear;
                if (child.ageTracker.AgeBiologicalYearsFloat >= 13f)
                    throw new InvalidOperationException("The child is not a child.");
                return new { success = true, researcher = researcher.GetUniqueLoadID(), child = child.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/drinker_and_child", Description = "UNSAFE FOR MODEL EXECUTION. Keep two paused disposable colonists: an adult with no chemical trait, tolerance or addiction, and a child aged eight (#1537).")]
        public async Task<object> DrinkerAndChild(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.drugs != null)
                    .OrderBy(p => p.thingIDNumber).ToList();
                var adult = pawns.First();
                var child = pawns.First(p => p != adult);
                foreach (var other in map.mapPawns.FreeColonistsSpawned.Where(p => p != adult && p != child).ToList())
                    other.Destroy(DestroyMode.Vanish);
                foreach (var trait in adult.story.traits.allTraits.Where(t => t.def.defName == "DrugDesire").ToList())
                    adult.story.traits.RemoveTrait(trait);
                foreach (var hediff in adult.health.hediffSet.hediffs.Where(h => h is Hediff_Addiction || h.def.defName.EndsWith("Tolerance")).ToList())
                    adult.health.RemoveHediff(hediff);
                if (adult.ageTracker.AgeBiologicalYearsFloat < 13f)
                    adult.ageTracker.AgeBiologicalTicks = 30L * GenDate.TicksPerYear;
                child.ageTracker.AgeBiologicalTicks = 8L * GenDate.TicksPerYear;
                if (child.ageTracker.AgeBiologicalYearsFloat >= 13f)
                    throw new InvalidOperationException("The child is not a child.");
                return new { success = true, adult = adult.GetUniqueLoadID(), child = child.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/luciferium_addict", Description = "UNSAFE FOR MODEL EXECUTION. Keep one paused disposable colonist, addicted to luciferium at severity 0.5, with ten luciferium beside it (#1538).")]
        public async Task<object> LuciferiumAddict(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.drugs != null)
                    .OrderBy(p => p.thingIDNumber).First();
                foreach (var other in map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn).ToList())
                    other.Destroy(DestroyMode.Vanish);
                var addiction = HediffMaker.MakeHediff(HediffDef.Named("LuciferiumAddiction"), pawn);
                addiction.Severity = 0.5f;
                pawn.health.AddHediff(addiction);
                var drug = ThingMaker.MakeThing(ThingDef.Named("Luciferium"));
                drug.stackCount = 10;
                if (!GenPlace.TryPlaceThing(drug, pawn.Position, map, ThingPlaceMode.Near))
                    throw new InvalidOperationException("The luciferium was not placed.");
                return new { success = true, pawn = pawn.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/cannibal_and_vegetarian", Description = "UNSAFE FOR MODEL EXECUTION. Keep two paused disposable colonists: a Cannibal-trait one on the colony ideoligion stripped of meat-eating precepts, and one moved to a fresh ideoligion with MeatEating_Abhorrent (#1541). Needs Ideology.")]
        public async Task<object> CannibalAndVegetarian(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                if (!ModsConfig.IdeologyActive)
                    throw new InvalidOperationException("Ideology is required.");
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.foodRestriction != null && p.ideo != null && p.story != null)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (pawns.Count < 2) throw new InvalidOperationException("Two adult colonists are required.");
                var cannibal = pawns[0]; var vegetarian = pawns[1];
                foreach (var other in map.mapPawns.FreeColonistsSpawned.Where(p => p != cannibal && p != vegetarian).ToList())
                    other.Destroy(DestroyMode.Vanish);
                var meat = PreceptDefOf.MeatEating_Abhorrent.issue;
                void Strip(Ideo ideo) {
                    foreach (var p in ideo.PreceptsListForReading.Where(p => p.def.issue == meat).ToList()) ideo.RemovePrecept(p);
                }
                var colony = cannibal.Ideo ?? throw new InvalidOperationException("The cannibal has no ideoligion.");
                Strip(colony);
                if (!cannibal.story.traits.HasTrait(DefDatabase<TraitDef>.GetNamed("Cannibal")))
                    cannibal.story.traits.GainTrait(new Trait(DefDatabase<TraitDef>.GetNamed("Cannibal"), 0, true));
                var veg = IdeoGenerator.GenerateIdeo(new IdeoGenerationParms(Faction.OfPlayer.def));
                Strip(veg);
                veg.AddPrecept(PreceptMaker.MakePrecept(PreceptDefOf.MeatEating_Abhorrent), true);
                Find.IdeoManager.Add(veg);
                vegetarian.ideo.SetIdeo(veg);
                if (vegetarian.Ideo != veg || !veg.HasPrecept(PreceptDefOf.MeatEating_Abhorrent))
                    throw new InvalidOperationException("The vegetarian ideoligion did not take.");
                return new { success = true, cannibal = cannibal.GetUniqueLoadID(), vegetarian = vegetarian.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/tame_dog", Description = "UNSAFE FOR MODEL EXECUTION. Spawn a named tame adult Husky beside a paused disposable colony's first colonist (#1543).")]
        public async Task<object> TameDog(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var colonist = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault()
                    ?? throw new InvalidOperationException("A colonist is required.");
                var dog = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Husky"), Faction.OfPlayer, fixedBiologicalAge: 4));
                dog.Name = new NameSingle("Biscuit");
                GenSpawn.Spawn(dog, CellFinder.StandableCellNear(colonist.Position, map, 5), map);
                if (dog.foodRestriction == null) throw new InvalidOperationException("The dog has no food policy tracker.");
                return new { success = true, animal = dog.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/duplicate_nickname", Description = "UNSAFE FOR MODEL EXECUTION. Give the newest of two paused disposable colonists the oldest one's nickname (#1310).")]
        public async Task<object> DuplicateNickname(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var named = map.mapPawns.FreeColonistsSpawned.Where(p => p.Name is NameTriple).OrderBy(p => p.thingIDNumber).ToList();
                if (named.Count < 2) throw new InvalidOperationException("Two named colonists are required.");
                var older = named[0]; var newer = named[named.Count - 1];
                var nick = older.Name.ToStringShort;
                var triple = (NameTriple)newer.Name;
                newer.Name = new NameTriple(triple.First, nick, triple.Last);
                return new { success = true, pawn = newer.GetUniqueLoadID(), keeper = older.GetUniqueLoadID(), name = nick };
            }, cancellationToken);
        }
        [Tool("test/doctor_medicine", Description = "UNSAFE FOR MODEL EXECUTION. Make one paused disposable colonist a doctor on NormalOrWorse care carrying no medicine, and place 60 herbal and 60 industrial medicine beside it (#1307).")]
        public async Task<object> DoctorMedicine(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && p.playerSettings != null && p.inventoryStock != null && p.workSettings != null
                        && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                pawn.workSettings.EnableAndInitialize();
                pawn.workSettings.SetPriority(WorkTypeDefOf.Doctor, 1);
                pawn.playerSettings.medCare = MedicalCareCategory.NormalOrWorse;
                pawn.inventoryStock.SetCountForGroup(InventoryStockGroupDefOf.Medicine, 0);
                foreach (var def in new[] { ThingDefOf.MedicineHerbal, ThingDefOf.MedicineIndustrial })
                {
                    var stack = ThingMaker.MakeThing(def);
                    stack.stackCount = 60;
                    GenPlace.TryPlaceThing(stack, pawn.Position, map, ThingPlaceMode.Near);
                    stack.SetForbidden(false, false);
                }
                return new { success = true, pawn = pawn.GetUniqueLoadID() };
            }, cancellationToken);
        }
        private static IEnumerable<Pawn> MoodColonists(Map map, string id) =>
            map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && p.needs?.mood != null && (string.IsNullOrEmpty(id) || p.GetUniqueLoadID() == id))
                .OrderBy(p => p.thingIDNumber);

        private static object[] Memories(Pawn pawn) =>
            pawn.needs.mood.thoughts.memories.Memories.Select(m => (object)new { def = m.def.defName, offset = m.MoodOffset() }).ToArray();

        // Mood is pinned natively with no clock: the unfrozen Mood need seeks the instantaneous level (base plus every thought), so a
        // calibrating memory whose moodPowerFactor makes that level land exactly on the target keeps CurLevel there through ordinary ticks (#2549).
        [Tool("test/mood_headroom", Description = "UNSAFE FOR MODEL EXECUTION. Hold one disposable colonist's mood exactly `headroom` above its native minor-break threshold with a calibrating memory (MyOrganHarvested to lower, Catharsis to raise), replacing any earlier calibration (#2549).")]
        public async Task<object> Headroom(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Pawn load id; empty takes the lowest-id colonist.")] string pawn,
            [ToolParameter(Description = "Mood level above the minor-break threshold, e.g. 0.07.")] double headroom)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded colony is required.");
                var p = MoodColonists(map, pawn).FirstOrDefault(x => !x.Downed && !x.InMentalState) ?? throw new InvalidOperationException("No eligible colonist.");
                var mood = p.needs.mood;
                var lower = ThoughtDef.Named("MyOrganHarvested");
                var raise = ThoughtDef.Named("Catharsis");
                mood.thoughts.memories.RemoveMemoriesOfDef(lower);
                mood.thoughts.memories.RemoveMemoriesOfDef(raise);
                mood.thoughts.situational.Notify_SituationalThoughtsDirty();
                var threshold = p.mindState.mentalBreaker.BreakThresholdMinor;
                var difficulty = Find.Storyteller.difficulty.colonistMoodOffset;
                var baseLevel = p.health.hediffSet.OverrideMoodBase ?? mood.def.baseLevel;
                var target = threshold + (float)headroom;
                var extra = (target - baseLevel) * 100f - mood.thoughts.TotalMoodOffset() - difficulty;
                var def = extra < 0f ? lower : raise;
                var memory = (Thought_Memory)ThoughtMaker.MakeThought(def);
                memory.moodPowerFactor = extra / def.stages[0].baseMoodEffect;
                mood.thoughts.memories.TryGainMemory(memory);
                mood.CurLevel = target;
                var instant = UnityEngine.Mathf.Clamp01(baseLevel + (mood.thoughts.TotalMoodOffset() + difficulty) / 100f);
                if (UnityEngine.Mathf.Abs(instant - target) > 0.005f)
                    throw new InvalidOperationException($"Mood did not calibrate: instant {instant} target {target}.");
                return new { success = true, pawn = p.GetUniqueLoadID(), threshold, mood = mood.CurLevel, instant, headroom = instant - threshold, calibrator = def.defName };
            }, cancellationToken);
        }

        [Tool("test/mood_thoughts", Description = "UNSAFE FOR MODEL EXECUTION. Give one colonist (or every colonist when pawn is empty) a named memory thought scaled to exactly `loss` mood points, or clear that def; replies each colonist's memories (#2549).")]
        public async Task<object> Thoughts(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Pawn load id, or first for the lowest-id colonist; empty means every free colonist.")] string pawn,
            [ToolParameter(Description = "Memory ThoughtDef defName, e.g. SleptOutside.")] string thought,
            [ToolParameter(Description = "Mood points the memory should carry (positive magnitude; the def's own sign is kept).")] double loss,
            [ToolParameter(Description = "Remove the def's memories instead of adding.")] bool clear = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded colony is required.");
                var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(thought);
                if (def == null || !def.IsMemory) throw new InvalidOperationException("Unknown memory thought.");
                var pawns = MoodColonists(map, pawn == "first" ? "" : pawn).Take(pawn == "first" ? 1 : int.MaxValue).ToList();
                if (pawns.Count == 0) throw new InvalidOperationException("No colonist.");
                foreach (var p in pawns)
                {
                    p.needs.mood.thoughts.memories.RemoveMemoriesOfDef(def);
                    if (clear) continue;
                    var memory = (Thought_Memory)ThoughtMaker.MakeThought(def);
                    memory.moodPowerFactor = (float)loss / Math.Abs(def.stages[0].baseMoodEffect);
                    p.needs.mood.thoughts.memories.TryGainMemory(memory);
                    if (!p.needs.mood.thoughts.memories.Memories.Any(m => m.def == def))
                        throw new InvalidOperationException($"{thought} was not kept by {p.LabelShort} (nullified).");
                }
                return new { success = true, pawns = pawns.Select(p => new { pawn = p.GetUniqueLoadID(), memories = Memories(p) }).ToArray() };
            }, cancellationToken);
        }

        [Tool("test/party_spot", Description = "UNSAFE FOR MODEL EXECUTION. Build one colony PartySpot on a standable cell beside the lowest-id colonist (unless one stands) and reply the cell (#2549).")]
        public async Task<object> PartySpot(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded colony is required.");
                var def = ThingDef.Named("PartySpot");
                var existing = map.listerBuildings.AllBuildingsColonistOfDef(def).FirstOrDefault();
                if (existing == null)
                {
                    var colonist = MoodColonists(map, "").First();
                    var cell = CellFinder.StandableCellNear(colonist.Position, map, 8);
                    var spot = ThingMaker.MakeThing(def);
                    spot.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(spot, cell, map);
                    existing = spot as Building ?? throw new InvalidOperationException("The PartySpot is not a building.");
                }
                return new { success = true, x = existing.Position.x, z = existing.Position.z, spots = map.listerBuildings.AllBuildingsColonistOfDef(def).Count() };
            }, cancellationToken);
        }

        // Native read of the claim #2549 (c) makes: how many lord jobs of one GatheringDef run, plus the game's own gate readings so a
        // refused start names which gate failed.
        [Tool("test/gathering_lords", Description = "UNSAFE FOR MODEL EXECUTION. Count running lord jobs of one GatheringDef (organizer and spot of the first) and read the game's gathering gates (#2549).")]
        public async Task<object> GatheringLords(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "GatheringDef defName, e.g. Party.")] string def)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded colony is required.");
                var gathering = DefDatabase<GatheringDef>.GetNamedSilentFail(def) ?? throw new InvalidOperationException("Unknown gathering def.");
                var jobs = Find.Maps.SelectMany(m => m.lordManager.lords).Select(l => l.LordJob as LordJob_Joinable_Gathering)
                    .Where(j => j != null && Traverse.Create(j).Field("gatheringDef").GetValue<GatheringDef>() == gathering).ToList();
                var colonists = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                var organizer = colonists.FirstOrDefault();
                var spotDef = gathering.gatherSpotDefs?.FirstOrDefault();
                var memory = ThoughtDef.Named("AttendedParty");
                return new {
                    success = true,
                    lords = jobs.Count,
                    organizer = jobs.Count > 0 ? jobs[0].Organizer?.GetUniqueLoadID() : null,
                    spotX = jobs.Count > 0 ? jobs[0].Spot.x : -1,
                    spotZ = jobs.Count > 0 ? jobs[0].Spot.z : -1,
                    colonists = colonists.Count,
                    spots = spotDef == null ? 0 : map.listerBuildings.AllBuildingsColonistOfDef(spotDef).Count(),
                    hour = GenLocalDate.HourInteger(map),
                    acceptable = GatheringsUtility.AcceptableGameConditionsToStartGathering(map, gathering),
                    canExecute = organizer != null && gathering.CanExecute(map, organizer),
                    attendedMemories = colonists.Sum(p => p.needs?.mood?.thoughts.memories.Memories.Count(m => m.def == memory) ?? 0),
                    ideology = ModsConfig.IdeologyActive,
                };
            }, cancellationToken);
        }

        [Tool("test/mood_setup",Description = "Seed deficient needs in one disposable pawn; test builds only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "food, rest, joy, forced, schedule, mental or environment.")] string scenario)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var p = map.mapPawns.FreeColonistsSpawned.First(x => !x.Dead && !x.Downed && x.needs?.mood != null && x.needs.joy != null);
                if (p.InMentalState) p.MentalState.RecoverFromState();
                p.drafter.Drafted = false;
                for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, scenario == "schedule" ? TimeAssignmentDefOf.Work : TimeAssignmentDefOf.Anything);
                p.needs.food.CurLevelPercentage = scenario == "food" ? 0.1f : 0.9f;
                p.needs.rest.CurLevelPercentage = scenario == "rest" ? 0.1f : 0.9f;
                p.needs.joy.CurLevelPercentage = scenario == "rest" || scenario == "food" ? 0.9f : 0.1f;
                p.needs.mood.CurLevelPercentage = p.mindState.mentalBreaker.BreakThresholdMinor - 0.01f;
                // The environment scenario leaves the pawn free to recover on
                // its own (a long Wait would keep it from playing); the others
                // pin a known current job for the relief fencing.
                var wait = JobMaker.MakeJob(JobDefOf.Wait, scenario == "environment" ? 60 : 25000);
                wait.playerForced = scenario == "forced";
                p.jobs.StartJob(wait, JobCondition.InterruptForced);
                if (scenario == "environment")
                {
                    // Removable environment pressure: a SleptOutside
                    // memory plus the NeedJoy situational thought the joy
                    // level above triggers, recalculated now so the first
                    // social read already carries it.
                    p.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.SleptOutside);
                    p.needs.mood.thoughts.situational.Notify_SituationalThoughtsDirty();
                    p.needs.mood.thoughts.TotalMoodOffset();
                }
                if (scenario == "food")
                {
                    var meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                    meal.stackCount = 5;
                    GenPlace.TryPlaceThing(meal, p.Position, map, ThingPlaceMode.Near);
                    meal.SetForbidden(false, false);
                }
                else if (scenario != "rest")
                    EnsureJoySource(map, p);
                if (scenario == "mental")
                    p.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forceWake: true);
                return new { success = true, pawn = p.GetUniqueLoadID(), scenario,
                    setup = "Test-only need/timetable/job setup; no production game tools mutate needs or mental states." };
            }, cancellationToken);
        }

        // The harnesses run against a fresh debug-start colony  that
        // owns no recreation building, and the only building-free joy giver
        // (skygazing) depends on daylight and clear weather, so JobGiver_GetJoy
        // would have nothing deterministic to issue for the joy scenarios. A
        // horseshoes pin (Core, JoyGiver_WatchBuilding: the pawn stands five
        // cells away in a three-wide rect) on cleared unroofed ground near the
        // pawn gives native relief a real job, the same way the food scenario
        // drops a meal beside them.
        private static void EnsureJoySource(Map map, Pawn p)
        {
            var pinDef = DefDatabase<ThingDef>.GetNamed("HorseshoesPin");
            if (map.listerThings.ThingsOfDef(pinDef).Any(t => t.Faction == Faction.OfPlayerSilentFail && !t.IsForbidden(p))) return;
            var center = FindPinCell(map, p);
            foreach (var c in CellRect.CenteredOn(center, 7).ClipInsideMap(map))
                foreach (var t in c.GetThingList(map).Where(Clearable).ToList()) t.Destroy();
            var pin = ThingMaker.MakeThing(pinDef, ThingDefOf.WoodLog);
            pin.SetFaction(Faction.OfPlayerSilentFail);
            GenSpawn.Spawn(pin, center, map);
            pin.SetForbidden(false, false);
        }

        // Plants, items and filth get cleared before the pin spawns, so they
        // must not disqualify a cell; only terrain, edifices and non-clearable
        // impassable things do. The fresh debug-start map has no
        // guaranteed 7x7 patch of bare standable ground within 30 cells of the
        // pawn, so the search widens in radius and shrinks the rect before
        // falling back to the pawn's own cell rather than throwing.
        private static IntVec3 FindPinCell(Map map, Pawn p)
        {
            foreach (var size in new[] { 7, 5, 3 })
                foreach (var radius in new[] { 30f, 55f })
                {
                    var found = GenRadial.RadialCellsAround(p.Position, radius, true)
                        .FirstOrDefault(c => CellRect.CenteredOn(c, size).Cells.All(v => PinFriendly(map, v)));
                    if (found.IsValid) return found;
                }
            var any = GenRadial.RadialCellsAround(p.Position, 55f, true).FirstOrDefault(c => PinFriendly(map, c));
            return any.IsValid ? any : p.Position;
        }

        private static bool PinFriendly(Map map, IntVec3 v)
        {
            if (!v.InBounds(map) || v.Fogged(map) || v.Roofed(map)) return false;
            if (v.GetTerrain(map).passability == Traversability.Impassable) return false;
            if (v.GetEdifice(map) != null || map.zoneManager.ZoneAt(v) != null) return false;
            return v.GetThingList(map).All(t => Clearable(t) || t.def.passability != Traversability.Impassable);
        }

        private static bool Clearable(Thing t) =>
            t is Plant || t is Filth || t.def.category == ThingCategory.Item;
    }
}
