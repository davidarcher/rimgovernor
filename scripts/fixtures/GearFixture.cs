using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private static Pawn shareBare;
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
            [ToolParameter(Description = "Door cell z on the hut's ring.")] int doorZ = -1,
            [ToolParameter(Description = "share_setup: the unique load id of the colonist who keeps the armor upgrade (the bedroom owner).")] string pawn = "",
            [ToolParameter(Description = "share_setup: rich (gold and food stocked) or poor (every loose item but wood and food destroyed).")] string wealth = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (mode == "share_setup" || mode == "share_read") {
                    // upkeep/personal-share-rich and -poor (#1847, epic #1829).
                    var map = Find.CurrentMap;
                    if (mode == "share_setup") {
                        if (wealth != "rich" && wealth != "poor") return new { success = false, error = "wealth must be rich or poor" };
                        subject = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawn);
                        var bare = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p != subject && p.apparel != null && !p.Downed);
                        if (subject == null || bare == null) return new { success = false, error = "no subject or second colonist" };
                        shareBare = bare;
                        if (wealth == "poor") {
                            foreach (var item in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => t.def != ThingDefOf.WoodLog && !t.def.IsIngestible && t.Spawned).ToList()) item.Destroy();
                        } else {
                            var center = subject.Position;
                            foreach (var (name, total) in new[] { ("Gold", 4000), ("Pemmican", 300) }) {
                                var def = DefDatabase<ThingDef>.GetNamed(name);
                                for (var left = total; left > 0;) {
                                    var stack = ThingMaker.MakeThing(def); stack.stackCount = System.Math.Min(def.stackLimit, left); left -= stack.stackCount;
                                    if (!GenPlace.TryPlaceThing(stack, center, map, ThingPlaceMode.Near)) return new { success = false, error = "stock placement failed" };
                                    stack.SetForbidden(false, false);
                                }
                            }
                        }
                        foreach (var p in new[] { subject, bare }) {
                            p.drafter.Drafted = false;
                            p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                            foreach (var a in p.apparel.WornApparel.ToList()) a.Destroy();
                            p.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame + 600000;
                        }
                        // The subject is dressed, a mid-rung flak vest over it; an excellent plate armor lies nearby.
                        foreach (var name in new[] { "Apparel_BasicShirt", "Apparel_Pants", "Apparel_FlakVest" }) {
                            var def = DefDatabase<ThingDef>.GetNamed(name);
                            var worn = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Cloth : null);
                            subject.apparel.Wear(worn, false);
                        }
                        var plateDef = DefDatabase<ThingDef>.GetNamed("Apparel_PlateArmor");
                        var plate = (Apparel)ThingMaker.MakeThing(plateDef, plateDef.MadeFromStuff ? ThingDefOf.Steel : null);
                        plate.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Colony);
                        GenPlace.TryPlaceThing(plate, subject.Position, map, ThingPlaceMode.Near);
                        plate.SetForbidden(false, false);
                        // The second colonist is bare: a basic shirt and pants are the necessity on the ground.
                        foreach (var name in new[] { "Apparel_BasicShirt", "Apparel_Pants" }) {
                            var def = DefDatabase<ThingDef>.GetNamed(name);
                            var cloth = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Cloth : null);
                            GenPlace.TryPlaceThing(cloth, bare.Position, map, ThingPlaceMode.Near);
                            cloth.SetForbidden(false, false);
                        }
                        foreach (var p in new[] { subject, bare }) {
                            var filter = p.outfits.CurrentApparelPolicy.filter;
                            foreach (var name in new[] { "Apparel_BasicShirt", "Apparel_Pants", "Apparel_FlakVest", "Apparel_PlateArmor" })
                                filter.SetAllow(DefDatabase<ThingDef>.GetNamed(name), true);
                        }
                        map.wealthWatcher.ForceRecount();
                    }
                    if (subject == null || shareBare == null) return new { success = false, error = "share_setup first" };
                    object Worn(Pawn p) => p.apparel.WornApparel.Select(a => {
                        a.TryGetQuality(out var q);
                        return new { defName = a.def.defName, quality = q.ToString(), hitPoints = a.HitPoints, maxHitPoints = a.MaxHitPoints };
                    }).ToArray();
                    return new { success = true, wealth, pawn = subject.GetUniqueLoadID(), bare = shareBare.GetUniqueLoadID(),
                        worn = Worn(subject), bareWorn = Worn(shareBare), wealthItems = map.wealthWatcher.WealthItems, wealthBuildings = map.wealthWatcher.WealthBuildings };
                }
                if (mode == "prune_setup" || mode == "prune_read") {
                    // apply/policy-prune (#1298): an extra outfit and an extra
                    // allowed area, the subject assigned to both.
                    var map = Find.CurrentMap;
                    if (mode == "prune_setup") {
                        subject = map.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && !p.InMentalState);
                        var extra = Current.Game.outfitDatabase.MakeNewOutfit(); extra.label = "Prune extra";
                        subject.outfits.CurrentApparelPolicy = extra;
                        var area = new Area_Allowed(map.areaManager, "Prune extra");
                        map.areaManager.AllAreas.Add(area);
                        area[subject.Position] = true;
                        subject.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                    }
                    var outfitNow = subject.outfits.CurrentApparelPolicy;
                    var areaNow = subject.playerSettings.AreaRestrictionInPawnCurrentMap;
                    return new { success = true, pawn = subject.GetUniqueLoadID(), shortName = subject.LabelShort,
                        outfit = outfitNow?.GetUniqueLoadID() ?? "", outfitLabel = outfitNow?.label ?? "", area = areaNow?.GetUniqueLoadID() ?? "",
                        outfits = Current.Game.outfitDatabase.AllOutfits.Select(o => o.GetUniqueLoadID()).ToArray(),
                        areas = map.areaManager.AllAreas.OfType<Area_Allowed>().Select(a => a.GetUniqueLoadID()).ToArray() };
                }
                if (mode == "outfits") {
                    // production/per-pawn-outfits (#1302): every free colonist's
                    // outfit and the whole outfit database.
                    return new { success = true,
                        pawns = Find.CurrentMap.mapPawns.FreeColonistsSpawned.Select(p => new { pawn = p.GetUniqueLoadID(), shortName = NativeApparelPolicyOperations.ShortName(p),
                            outfit = p.outfits?.CurrentApparelPolicy?.GetUniqueLoadID() ?? "", label = p.outfits?.CurrentApparelPolicy?.label ?? "" }).ToArray(),
                        outfits = Current.Game.outfitDatabase.AllOutfits.Select(o => o.GetUniqueLoadID()).ToArray() };
                }
                if (mode.StartsWith("policy_")) {
                    if (mode == "policy_setup") {
                        subject = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && !p.InMentalState);
                        subject.drafter.Drafted = false;
                        var player = Current.Game.outfitDatabase.MakeNewOutfit(); player.label = "Player custom";
                        subject.outfits.CurrentApparelPolicy = player;
                        var shirt = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_BasicShirt"), ThingDefOf.Cloth);
                        foreach (var old in subject.apparel.WornApparel.ToList()) old.Destroy();
                        subject.apparel.Wear(shirt); subject.outfits.forcedHandler.SetForced(shirt, true); subject.apparel.Lock(shirt);
                    }
                    if (mode == "policy_edit") subject.outfits.CurrentApparelPolicy.filter.SetAllow(SpecialThingFilterDefOf.AllowDeadmansApparel, true);
                    var outfit = subject.outfits.CurrentApparelPolicy;
                    return new { success = true, pawn = subject.GetUniqueLoadID(), shortName = NativeApparelPolicyOperations.ShortName(subject), token = NativeApparelPolicyOperations.Token(subject),
                        policy = outfit.id, name = outfit.label, defs = outfit.filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d).ToArray(),
                        minHP = outfit.filter.AllowedHitPointsPercents.min, maxHP = outfit.filter.AllowedHitPointsPercents.max,
                        minQuality = (int)outfit.filter.AllowedQualityLevels.min, maxQuality = (int)outfit.filter.AllowedQualityLevels.max,
                        tainted = outfit.filter.Allows(SpecialThingFilterDefOf.AllowDeadmansApparel), clean = outfit.filter.Allows(SpecialThingFilterDefOf.AllowNonDeadmansApparel),
                        forced = subject.outfits.forcedHandler.ForcedApparel.Count, locked = subject.apparel.AnyApparelLocked,
                        count = Current.Game.outfitDatabase.AllOutfits.Count };
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
                        var tailoring = DefDatabase<WorkTypeDef>.GetNamed("Tailoring");
                        if (!worker.WorkTypeIsDisabled(tailoring)) worker.workSettings.SetPriority(tailoring, 1);
                    }
                    // Vanilla's apparel optimizer would dress the subject in the produced garment on its own
                    // within an in-game hour or two; hold it off so the controller's wear order is what dresses
                    // the pawn and the case proves the whole produce-then-equip path (issue #233).
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
}
