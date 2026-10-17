using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Disposable scenario setup only; default companion builds exclude this tool.
    public sealed class GearFixture
    {
        private static Pawn subject;
        private static Apparel replacement;
        private static Apparel damaged;
        private static ThingWithComps weapon;
        [Tool("test/gear_fixture", Description = "Set up disposable apparel acceptance conditions.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "setup, force, unforce, forbid, allow, policy or interrupt")] string mode = "setup",
            [ToolParameter(Description = "production_setup: the ingredient stack placed by the subject (Cloth or a leather def).")] string material = "Cloth",
            [ToolParameter(Description = "South-west corner x of the 11x11 hut the controller's starter search chose (required).")] int siteX = -1,
            [ToolParameter(Description = "South-west corner z of the hut.")] int siteZ = -1,
            [ToolParameter(Description = "Door cell x on the hut's ring; negative puts the door mid east wall.")] int doorX = -1,
            [ToolParameter(Description = "Door cell z on the hut's ring.")] int doorZ = -1)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (mode == "prune_setup") {
                    // apply/policy-prune: an extra outfit and an extra
                    // allowed area, the subject assigned to both.
                    var map = Find.CurrentMap;
                    subject = map.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && !p.InMentalState);
                    var extra = Current.Game.outfitDatabase.MakeNewOutfit(); extra.label = "Prune extra";
                    subject.outfits.CurrentApparelPolicy = extra;
                    var area = new Area_Allowed(map.areaManager, "Prune extra");
                    map.areaManager.AllAreas.Add(area);
                    area[subject.Position] = true;
                    subject.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                    return FixtureKit.Reply(new GearPruneReply { Success = true, Pawn = subject.GetUniqueLoadID(), ShortName = NativeApparelPolicyOperations.ShortName(subject),
                        Outfit = extra.GetUniqueLoadID(), Area = area.GetUniqueLoadID() });
                }
                if (mode == "policy_setup" || mode == "policy_edit") {
                    // production/apparel-policy: a player outfit and forced,
                    // locked apparel the primitives cannot stage.
                    if (mode == "policy_setup") {
                        subject = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && !p.InMentalState);
                        subject.drafter.Drafted = false;
                        var player = Current.Game.outfitDatabase.MakeNewOutfit(); player.label = "Player custom";
                        subject.outfits.CurrentApparelPolicy = player;
                        var shirt = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_BasicShirt"), ThingDefOf.Cloth);
                        foreach (var old in subject.apparel.WornApparel.ToList()) old.Destroy();
                        subject.apparel.Wear(shirt); subject.outfits.forcedHandler.SetForced(shirt, true); subject.apparel.Lock(shirt);
                    }
                    else subject.outfits.CurrentApparelPolicy.filter.SetAllow(SpecialThingFilterDefOf.AllowDeadmansApparel, true);
                    return FixtureKit.Reply(new GearPolicyReply { Success = true, Pawn = subject.GetUniqueLoadID(), ShortName = NativeApparelPolicyOperations.ShortName(subject) });
                }
                if (mode == "weapon_setup" || mode == "weapon_assigned" || mode == "weapon_damage") {
                    var map = Find.CurrentMap;
                    subject = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTagIsDisabled(WorkTags.Violent)
                        && !p.WorkTagIsDisabled(WorkTags.Shooting) && !p.Downed && !p.InMentalState);
                    subject.drafter.Drafted = false;
                    subject.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    if (mode != "weapon_damage") subject.equipment.Primary?.Destroy();
                    else subject.equipment.Primary.HitPoints = (int)(subject.equipment.Primary.MaxHitPoints * .3f);
                    var def = DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle");
                    weapon = (ThingWithComps)ThingMaker.MakeThing(def);
                    var cell = GenRadial.RadialCellsAround(subject.Position, 5, false).First(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map));
                    GenSpawn.Spawn(weapon, cell, map);
                    weapon.SetForbidden(false);
                    if (mode == "weapon_assigned") {
                        var assigned = (ThingWithComps)ThingMaker.MakeThing(def);
                        assigned.HitPoints = (int)(assigned.MaxHitPoints * .3f);
                        subject.equipment.AddEquipment(assigned);
                    }
                    return new { success = true, pawn = subject.GetUniqueLoadID(), target = weapon.GetUniqueLoadID() };
                }
                if (mode == "production_setup" || mode == "production_unstaged") {
                    var map = subject.Map;
                    foreach (var item in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).ToList()) item.Destroy();
                    Thing bench = null;
                    if (mode == "production_unstaged") {
                        foreach (var existing in map.listerBuildings.allBuildingsColonist
                            .Where(b => b.def.AllRecipes != null && b.def.AllRecipes.Any(r => r.products.Any(p => p.thingDef.defName == "Apparel_BasicShirt"))).ToList())
                            existing.Destroy();
                        var hut = FixtureHut.Build(map, 11, FixtureHut.Site(siteX, siteZ), FixtureHut.Site(doorX, doorZ));
                        FixtureHut.DropOutside(map, hut, ThingDefOf.WoodLog, 150);
                        FixtureHut.DropOutside(map, hut, ThingDefOf.Steel, 150);
                    } else {
                        bench = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("HandTailoringBench"), ThingDefOf.WoodLog);
                        bench.SetFaction(Faction.OfPlayer);
                        var cell = GenRadial.RadialCellsAround(subject.Position, 15, false).First(c => c.InBounds(map)
                            && CellRect.CenteredOn(c, 3).Cells.All(v => v.InBounds(map) && v.Standable(map) && v.GetEdifice(map) == null));
                        GenSpawn.Spawn(bench, cell, map);
                    }
                    var cloth = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(material));
                    cloth.stackCount = 75;
                    GenPlace.TryPlaceThing(cloth, subject.Position, map, ThingPlaceMode.Near);
                    cloth.SetForbidden(false);
                    foreach (var worker in map.mapPawns.FreeColonistsSpawned) {
                        if (worker != subject)
                            foreach (var worn in worker.apparel.WornApparel)
                                worker.outfits.forcedHandler.SetForced(worn, true);
                        var tailoring = ExtraWorkTypeDefOf.Tailoring;
                        if (!worker.WorkTypeIsDisabled(tailoring)) worker.workSettings.SetPriority(tailoring, 1);
                    }
                    // Vanilla's apparel optimizer would dress the subject in the produced garment on its own
                    // within an in-game hour or two; hold it off so the controller's wear order is what dresses
                    // the pawn and the case proves the whole produce-then-equip path.
                    subject.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame + 600000;
                    return new { success = true, pawn = subject.GetUniqueLoadID(), bench = bench?.GetUniqueLoadID(), cloth = cloth.GetUniqueLoadID(), material = cloth.def.defName,
                        worn = subject.apparel.WornApparel.Select(a => new { thingId = a.GetUniqueLoadID(), defName = a.def.defName, stuff = a.Stuff?.defName, hitPoints = a.HitPoints, maxHitPoints = a.MaxHitPoints }).ToList() };
                }
                if (mode == "production_research") {
                    var project = DefDatabase<ResearchProjectDef>.GetNamed("ComplexClothing");
                    Find.ResearchManager.FinishProject(project, false);
                    return new { success = project.IsFinished, research = project.defName };
                }
                if (mode == "incompatible") {
                    var def = DefDatabase<ThingDef>.AllDefs.First(d => d.IsApparel &&
                        !d.apparel.developmentalStageFilter.Has(subject.DevelopmentalStage));
                    var item = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.AllowedStuffsFor(def).First() : null);
                    GenPlace.TryPlaceThing(item, subject.Position, subject.Map, ThingPlaceMode.Near);
                    item.SetForbidden(false);
                    return new { success = true, pawn = subject.GetUniqueLoadID(), target = item.GetUniqueLoadID(), defName = def.defName };
                }
                if (mode == "setup" || mode == "armor_setup" || mode == "cold_setup" || mode == "heat_setup") {
                    var map = Find.CurrentMap;
                    subject = map.mapPawns.FreeColonistsSpawned.First(p => p.apparel != null && p.outfits != null && !p.Downed && !p.InMentalState);
                    subject.drafter.Drafted = false;
                    subject.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    foreach (var a in subject.apparel.WornApparel.ToList()) a.Destroy();
                    var initialDef = DefDatabase<ThingDef>.GetNamed(mode == "armor_setup" ? "Apparel_FlakVest" : "Apparel_BasicShirt");
                    damaged = (Apparel)ThingMaker.MakeThing(initialDef, initialDef.MadeFromStuff ? ThingDefOf.Cloth : null);
                    subject.apparel.Wear(damaged);
                    damaged.HitPoints = (int)(damaged.MaxHitPoints * (mode == "cold_setup" || mode == "heat_setup" ? 1f : .3f));
                    var def = DefDatabase<ThingDef>.GetNamed(mode == "cold_setup" ? "Apparel_Parka" :
                        mode == "heat_setup" ? "Apparel_Duster" : initialDef.defName);
                    replacement = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Cloth : null);
                    var cell = GenRadial.RadialCellsAround(subject.Position, 5, false).First(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map));
                    GenSpawn.Spawn(replacement, cell, map);
                    replacement.SetForbidden(false);
                    subject.outfits.CurrentApparelPolicy.filter.SetAllow(def, true);
                } else if (mode == "force") subject.outfits.forcedHandler.SetForced(damaged, true);
                else if (mode == "unforce") subject.outfits.forcedHandler.SetForced(damaged, false);
                else if (mode == "forbid") replacement.SetForbidden(true);
                else if (mode == "allow") replacement.SetForbidden(false);
                else if (mode == "policy") subject.outfits.CurrentApparelPolicy.filter.SetAllow(replacement.def, false);
                else if (mode == "interrupt") subject.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 600), JobCondition.InterruptForced);
                return new { success = true, pawn = subject.GetUniqueLoadID(), target = replacement.GetUniqueLoadID(), damaged = damaged.GetUniqueLoadID() };
            }, cancellationToken);
        }
    }

    // The DeadMansApparel probe (#3283): the first colonist (lowest thing id)
    // wears five fresh, mutually compatible garments, the first `tainted` of
    // them marked worn by a corpse through the native Apparel.WornByCorpse,
    // with the Bloodlust trait added or removed. Every call redresses the pawn,
    // so the worn count is always five and only the tainted count varies.
    public sealed class DeadMansFixture
    {
        private static readonly string[] Outfit = { "Apparel_BasicShirt", "Apparel_Pants", "Apparel_FlakVest", "Apparel_Duster", "Apparel_CowboyHat" };

        [Tool("test/deadmans_wear", Description = "Disposable fixture (#3283): redress the first colonist in five fresh garments, the first `tainted` worn by a corpse, with Bloodlust on or off. Test builds only.")]
        public async Task<object> Wear(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "How many of the five garments are WornByCorpse (0-5).")] int tainted = 0,
            [ToolParameter(Description = "Give the pawn the Bloodlust trait (true) or remove it (false).")] bool bloodlust = false)
            => await ctx.MainThread.InvokeAsync<object>(() =>
            {
                if (tainted < 0 || tainted > Outfit.Length) throw new System.ArgumentOutOfRangeException(nameof(tainted));
                var pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
                foreach (var old in pawn.apparel.WornApparel.ToList()) old.Destroy();
                var defs = Outfit.Select(n => DefDatabase<ThingDef>.GetNamed(n)).ToList();
                for (var i = 0; i < defs.Count; i++)
                {
                    if (!ApparelUtility.HasPartsToWear(pawn, defs[i])) throw new System.InvalidOperationException($"{pawn} cannot wear {defs[i].defName}.");
                    for (var j = 0; j < i; j++)
                        if (!ApparelUtility.CanWearTogether(defs[i], defs[j], pawn.RaceProps.body))
                            throw new System.InvalidOperationException($"{defs[i].defName} and {defs[j].defName} cannot be worn together.");
                }
                for (var i = 0; i < defs.Count; i++)
                {
                    var apparel = (Apparel)ThingMaker.MakeThing(defs[i], defs[i].MadeFromStuff ? GenStuff.DefaultStuffFor(defs[i]) : null);
                    apparel.WornByCorpse = i < tainted;
                    pawn.apparel.Wear(apparel, false, true);
                }
                var trait = pawn.story.traits.GetTrait(TraitDefOf.Bloodlust);
                if (bloodlust && trait == null) pawn.story.traits.GainTrait(new Trait(TraitDefOf.Bloodlust));
                if (!bloodlust && trait != null) pawn.story.traits.RemoveTrait(trait);
                // Vanilla's optimizer would strip tainted gear on its own; the probe holds the outfit still.
                pawn.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame + 600000;
                pawn.needs?.mood?.thoughts?.situational?.Notify_SituationalThoughtsDirty();
                var worn = pawn.apparel.WornApparel;
                return new { success = true, pawn = pawn.GetUniqueLoadID(),
                    worn = worn.Count, tainted = worn.Count(a => a.WornByCorpse),
                    bloodlust = pawn.story.traits.HasTrait(TraitDefOf.Bloodlust),
                    inhumanized = pawn.health.hediffSet.hediffs.Any(h => h.def.defName == "Inhumanized"),
                    traits = pawn.story.traits.allTraits.Select(t => t.def.defName).ToList() };
            }, cancellationToken);
    }
}
