using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
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
            [ToolParameter(Description = "setup, force, unforce, forbid, allow, policy or interrupt")] string mode = "setup")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
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
                if (mode == "production_setup") {
                    var map = subject.Map;
                    foreach (var shirt in map.listerThings.ThingsOfDef(replacement.def).ToList()) shirt.Destroy();
                    var bench = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("HandTailoringBench"), ThingDefOf.WoodLog);
                    bench.SetFaction(Faction.OfPlayer);
                    var cell = GenRadial.RadialCellsAround(subject.Position, 15, false).First(c => c.InBounds(map)
                        && CellRect.CenteredOn(c, 3).Cells.All(v => v.InBounds(map) && v.Standable(map) && v.GetEdifice(map) == null));
                    GenSpawn.Spawn(bench, cell, map);
                    var cloth = ThingMaker.MakeThing(ThingDefOf.Cloth);
                    cloth.stackCount = 75;
                    GenPlace.TryPlaceThing(cloth, subject.Position, map, ThingPlaceMode.Near);
                    cloth.SetForbidden(false);
                    foreach (var worker in map.mapPawns.FreeColonistsSpawned) {
                        var tailoring = DefDatabase<WorkTypeDef>.GetNamed("Tailoring");
                        if (!worker.WorkTypeIsDisabled(tailoring)) worker.workSettings.SetPriority(tailoring, 1);
                    }
                    return new { success = true, pawn = subject.GetUniqueLoadID(), bench = bench.GetUniqueLoadID(), cloth = cloth.GetUniqueLoadID() };
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
