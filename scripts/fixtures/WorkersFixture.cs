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
            [ToolParameter(Description = "passion, traits, coverage or nightowl.")] string scenario)
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
                    pawns = pawns.Select(p => new { id = p.GetUniqueLoadID(), name = p.LabelShort,
                        disabled = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => p.WorkTypeIsDisabled(w)).Select(w => w.defName).ToList(),
                        traits = p.story.traits.allTraits.Select(t => t.def.defName).ToList() }).ToList(),
                    setup = "Test-only skill/trait/timetable seeding; no production tool mutates a pawn's biography." };
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
