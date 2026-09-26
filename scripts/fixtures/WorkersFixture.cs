using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Test-build-only roster seeding for the workers/* cases (#417): the
    // three debug-start colonists get a flat skill sheet, no traits, the
    // native default timetable, manual priorities on and every enabled work
    // type at 3, then the scenario layers the skills, passions, traits or
    // timetable edit the case reads back through the ordinary pawn
    // observation and plans against. Nothing here is reachable from a
    // production build.
    public sealed class WorkersFixture
    {
        // Drug policy ownership for takeover/drug-policy (#495): "seed" moves
        // the first colonist onto a foreign policy and plants a drifted policy
        // under the routine's name; "drift" re-edits the routine's policy after
        // assignment. The controller owns every policy under autonomous control.
        [Tool("test/drug_policy", Description = "Test-only drug policy ownership fixture: seed, drift or read.")]
        public async Task<object> DrugPolicy(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "seed, drift or read")] string action)
        {
            const string foreignLabel = "test-foreign-drugs", routineLabel = "RimGovernor social drugs";
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var db = Current.Game.drugPolicyDatabase;
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.drugs != null)
                    .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                if (pawns.Count < 3) return new { success = false, error = "drug policy fixture needs three colonists, found " + pawns.Count };
                var tea = DefDatabase<ThingDef>.GetNamed("PsychiteTea");
                Func<string, DrugPolicy> named = label => db.AllPolicies.FirstOrDefault(p => p.label == label);
                if (action == "seed") {
                    Current.Game.playSettings.useWorkPriorities = true;
                    var foreign = named(foreignLabel);
                    if (foreign == null) { foreign = db.MakeNewDrugPolicy(); foreign.label = foreignLabel; }
                    foreign[tea].allowedForJoy = true;
                    pawns[0].drugs.CurrentPolicy = foreign;
                    var drifted = named(routineLabel);
                    if (drifted == null) { drifted = db.MakeNewDrugPolicy(); drifted.label = routineLabel; }
                    drifted[tea].allowedForJoy = true;
                } else if (action == "drift") {
                    named(routineLabel)[tea].allowedForJoy = true;
                } else if (action != "read") throw new ArgumentException("unknown drug fixture action");
                var routine = named(routineLabel);
                return new { success = true, pawns = pawns.Take(3).Select(p => p.GetUniqueLoadID()).ToList(),
                    labels = pawns.Take(3).Select(p => p.drugs.CurrentPolicy.label).ToList(),
                    defaultLabel = db.DefaultDrugPolicy().label,
                    routineCount = db.AllPolicies.Count(p => p.label == routineLabel),
                    routineId = routine?.id ?? 0, routineTea = routine?[tea].allowedForJoy ?? false,
                    routineBeer = routine?[ThingDefOf.Beer].allowedForJoy ?? false };
            }, cancellationToken);
        }

        [Tool("test/food_policy", Description = "Test-only restrictive diet and reachable meal fixture; read reports actual nutrition eaten.")]
        public async Task<object> FoodPolicy(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "restrict, edit or read")] string action)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.foodRestriction != null && p.needs?.food != null && p.workSettings?.EverWork == true)
                    .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).First();
                var database = Current.Game.foodRestrictionDatabase;
                var manual = database.AllFoodRestrictions.FirstOrDefault(p => p.label == "test-restrictive-food");
                if (action == "restrict") {
                    Current.Game.playSettings.useWorkPriorities = true;
                    if (manual == null) { manual = database.MakeNewFoodRestriction(); manual.label = "test-restrictive-food"; }
                    manual.filter.SetDisallowAll();
                    pawn.foodRestriction.CurrentFoodPolicy = manual;
                    pawn.drafter.Drafted = false;
                    if (pawn.InMentalState) pawn.MentalState.RecoverFromState();
                    pawn.jobs.StopAll();
                    pawn.needs.food.CurLevelPercentage = 0.1f;
                    pawn.needs.rest.CurLevelPercentage = 1f;
                    for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                    var meal = ThingMaker.MakeThing(ThingDefOf.MealSimple); meal.stackCount = 20;
                    GenPlace.TryPlaceThing(meal, pawn.Position, map, ThingPlaceMode.Near);
                    meal.SetForbidden(false, false);
                } else if (action == "edit") {
                    pawn.foodRestriction.CurrentFoodPolicy.filter.SetAllow(ThingDefOf.RawPotatoes, true);
                } else if (action != "read") throw new ArgumentException("unknown food fixture action");
                var current = pawn.foodRestriction.CurrentFoodPolicy;
                return new { pawn = pawn.GetUniqueLoadID(), policy = current.GetUniqueLoadID(),
                    allowed = current.filter.Allows(ThingDefOf.MealSimple),
                    manualStillRestricted = manual != null && !manual.filter.Allows(ThingDefOf.MealSimple),
                    food = pawn.needs.food.CurLevelPercentage, eaten = pawn.records.GetValue(RecordDefOf.NutritionEaten),
                    tick = Find.TickManager.TicksGame };
            }, cancellationToken);
        }

        [Tool("test/workers_setup", Description = "Seed skills, passions, traits and timetables on the three colonists for a workers/* scenario; test builds only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "passion, traits, coverage, helpers or nightowl.")] string scenario)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                // Fewest backstory-disabled work types first, so the roles the
                // scenarios lean on (A cooks, A/B build) land on the least
                // restricted pawns; ties by load id for a stable order.
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.workSettings != null && p.skills != null && p.story?.traits != null)
                    .OrderBy(p => DefDatabase<WorkTypeDef>.AllDefsListForReading.Count(w => p.WorkTypeIsDisabled(w))).ThenBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                if (pawns.Count < 3) return new { success = false, error = "workers fixture needs three free colonists, found " + pawns.Count };
                pawns = pawns.Take(3).ToList();
                Current.Game.playSettings.useWorkPriorities = true;
                foreach (var p in pawns)
                {
                    if (p.InMentalState) p.MentalState.RecoverFromState();
                    p.drafter.Drafted = false;
                    foreach (var trait in p.story.traits.allTraits.ToList()) p.story.traits.RemoveTrait(trait);
                    foreach (var skill in p.skills.skills) { skill.Level = 4; skill.passion = Passion.None; skill.xpSinceLastLevel = 0f; }
                    for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, i > 21 || i <= 5 ? TimeAssignmentDefOf.Sleep : TimeAssignmentDefOf.Anything);
                }
                var a = pawns[0]; var b = pawns[1]; var c = pawns[2];
                var helperCells = new List<IntVec3>();
                switch (scenario)
                {
                    case "passion":
                        Skill(a, SkillDefOf.Cooking, 12, Passion.Major);
                        Skill(b, SkillDefOf.Cooking, 12, Passion.None);
                        Skill(c, SkillDefOf.Cooking, 3, Passion.None);
                        break;
                    case "traits":
                        Gain(a, "Pyromaniac", 0); Gain(a, "Brawler", 0); Gain(a, "Abrasive", 0);
                        Gain(b, "Industriousness", 2);
                        Skill(b, SkillDefOf.Construction, 8, Passion.None);
                        Skill(c, SkillDefOf.Construction, 8, Passion.None);
                        break;
                    case "coverage":
                        break;
                    case "helpers":
                        // #653: one skilled builder and two idle pawns under
                        // the Construction floor beside six wood wall
                        // blueprints with the wood to build them.
                        Skill(a, SkillDefOf.Construction, 10, Passion.None);
                        Skill(b, SkillDefOf.Construction, 2, Passion.None);
                        Skill(c, SkillDefOf.Construction, 1, Passion.None);
                        foreach (var p in pawns) p.jobs.StopAll();
                        var origin = a.Position;
                        var placed = 0;
                        foreach (var cell in GenRadial.RadialCellsAround(origin, 12f, false))
                        {
                            if (placed >= 6) break;
                            if (!cell.InBounds(map) || !cell.Standable(map) || cell.GetFirstBuilding(map) != null || cell.GetThingList(map).Any(t => t is Pawn || t.def.category == ThingCategory.Item || t.def.IsBlueprint || t.def.IsFrame) || !GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, cell, Rot4.North, map, false, null, null, ThingDefOf.WoodLog).Accepted) continue;
                            if (cell.DistanceTo(origin) < 4f) continue;
                            GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, cell, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                            helperCells.Add(cell);
                            placed++;
                        }
                        var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog); wood.stackCount = 75;
                        GenPlace.TryPlaceThing(wood, origin, map, ThingPlaceMode.Near);
                        wood.SetForbidden(false, false);
                        break;
                    case "nightowl":
                        Gain(a, "NightOwl", 0);
                        Gain(b, "QuickSleeper", 0);
                        c.timetable.SetAssignment(12, TimeAssignmentDefOf.Joy);
                        break;
                    default:
                        return new { success = false, error = "unknown workers scenario " + scenario };
                }
                // Trait changes moved the disabled work tags; the priority
                // sheet then starts flat so the case's first plan has
                // something to write.
                foreach (var p in pawns)
                {
                    p.Notify_DisabledWorkTypesChanged();
                    p.skills.Notify_SkillDisablesChanged();
                    foreach (var w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!p.WorkTypeIsDisabled(w)) p.workSettings.SetPriority(w, 3);
                }
                return new { success = true, scenario, manual = true,
                    blueprints = helperCells.Select(cell => new { x = cell.x, z = cell.z }).ToList(),
                    pawns = pawns.Select(p => new { id = p.GetUniqueLoadID(), name = p.LabelShort,
                        disabled = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => p.WorkTypeIsDisabled(w)).Select(w => w.defName).ToList(),
                        traits = p.story.traits.allTraits.Select(t => t.def.defName).ToList() }).ToList(),
                    setup = "Test-only skill/trait/timetable seeding; no production tool mutates a pawn's biography." };
            }, cancellationToken);
        }

        // workers/helpers (#653): "occupy" drafts the skilled builder so only
        // the helpers can take the walls; "read" reports each pawn's native
        // ThingsConstructed record and current job, and how many of the
        // blueprint cells now hold a finished wall.
        [Tool("test/workers_helpers", Description = "Test-only workers/helpers probe: occupy drafts a pawn; read reports construction records and built walls.")]
        public async Task<object> Helpers(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "occupy or read")] string action,
            [ToolParameter(Description = "pawn load id to draft for occupy")] string pawn)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
                if (action == "occupy") {
                    var target = colonists.FirstOrDefault(p => p.GetUniqueLoadID() == pawn);
                    if (target == null) return new { success = false, error = "no colonist " + pawn };
                    target.drafter.Drafted = true;
                } else if (action != "read") throw new ArgumentException("unknown helpers fixture action");
                var walls = map.listerBuildings.allBuildingsColonist.Count(t => t.def == ThingDefOf.Wall && t.Stuff == ThingDefOf.WoodLog);
                return new { success = true, walls, tick = Find.TickManager.TicksGame,
                    pawns = colonists.Select(p => new { id = p.GetUniqueLoadID(), drafted = p.Drafted,
                        constructed = p.records.GetValue(RecordDefOf.ThingsConstructed),
                        job = p.CurJobDef?.defName ?? "", work = p.CurJob?.workGiverDef?.workType?.defName ?? "" }).ToList() };
            }, cancellationToken);
        }

        private static void Skill(Pawn p, SkillDef def, int level, Passion passion)
        {
            var skill = p.skills.GetSkill(def);
            skill.Level = level; skill.passion = passion; skill.xpSinceLastLevel = 0f;
        }

        private static void Gain(Pawn p, string def, int degree)
        {
            p.story.traits.GainTrait(new Trait(DefDatabase<TraitDef>.GetNamed(def), degree));
        }
    }
}
