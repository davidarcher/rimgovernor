using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Stages the gear/* area's
    // preconditions (#472) on the loaded tribal baseline: the late-autumn
    // colony with cloth and a tailoring bench (gear/winter), a tainted drop
    // beside clean stock (gear/tainted), two marksmen with the armor
    // research and materials funded (gear/soldier) and a stripped twelve-pawn
    // roster with spares in storage (gear/roster), plus the census probe the
    // cases audit against. Nothing here plans, orders or dresses anyone.
    public sealed class GearAreaFixture
    {
        private const int RosterSize = 12;

        [Tool("test/gear_area_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: stage one gear/* acceptance precondition on the loaded colony. mode is winter (calendar moved to hoursBeforeWinter hours before the first winter twelfth, cloth and a tailoring bench supplied), tainted (a tainted parka beside a clean one), soldier (two colonists with Shooting 12 and 8, Smithing/FlakArmor researched, steel funded, a bolt-action and a shotgun loose) or roster (twelve colonists each stripped of shirt and headgear, spares in a stockpile).")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "winter, tainted, soldier or roster")] string mode = "winter",
            [ToolParameter(Description = "winter: hours the calendar lands before the first winter twelfth")] int hoursBeforeWinter = 24)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var people = Colonists(map);
                if (people.Count == 0) return Refuse("No free colonists on the map.");
                switch (mode) {
                    case "winter": return Winter(map, people, hoursBeforeWinter);
                    case "tainted": return Tainted(map, people);
                    case "soldier": return Soldier(map, people);
                    case "roster": return Roster(map, people);
                }
                return Refuse("mode must be winter, tainted, soldier or roster: " + mode);
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/gear_area_probe", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: read every free colonist's worn apparel (with tainted, layers and body-part groups), primary weapon, apparel policy, Shooting level, comfort band and thermal hediffs, plus the apparel slot (layers|groups) of each definition named in defs. Read-only.")]
        public async Task<object> Probe(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "comma-separated apparel definitions whose slot to report")] string defs = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null) return Refuse("A disposable colony map is required.");
                var slots = new Dictionary<string, string>();
                foreach (var name in defs.Split(',').Select(d => d.Trim()).Where(d => d.Length > 0)) {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (def?.apparel != null) slots[name] = Slot(def);
                }
                var hypothermia = DefDatabase<HediffDef>.GetNamedSilentFail("Hypothermia");
                var heatstroke = DefDatabase<HediffDef>.GetNamedSilentFail("Heatstroke");
                var rows = Colonists(map).Select(p => new {
                    pawn = p.GetUniqueLoadID(), name = p.LabelShort,
                    policy = p.outfits?.CurrentApparelPolicy?.label,
                    policyId = p.outfits?.CurrentApparelPolicy?.GetUniqueLoadID(),
                    shooting = p.skills?.GetSkill(SkillDefOf.Shooting).Level ?? 0,
                    ambient = p.AmbientTemperature,
                    comfortableMin = p.GetStatValue(StatDefOf.ComfyTemperatureMin),
                    comfortableMax = p.GetStatValue(StatDefOf.ComfyTemperatureMax),
                    hypothermia = hypothermia == null ? 0f : (p.health.hediffSet.GetFirstHediffOfDef(hypothermia)?.Severity ?? 0f),
                    heatstroke = heatstroke == null ? 0f : (p.health.hediffSet.GetFirstHediffOfDef(heatstroke)?.Severity ?? 0f),
                    primary = p.equipment?.Primary == null ? null : new { thingId = p.equipment.Primary.GetUniqueLoadID(), defName = p.equipment.Primary.def.defName },
                    worn = (p.apparel?.WornApparel ?? new List<Apparel>()).Select(a => new {
                        thingId = a.GetUniqueLoadID(), defName = a.def.defName, stuff = a.Stuff?.defName, tainted = a.WornByCorpse,
                        hitPoints = a.HitPoints, maxHitPoints = a.MaxHitPoints, slot = Slot(a.def) }).ToList()
                }).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, colonists = rows, defSlots = slots };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Winter(Map map, List<Pawn> people, int hoursBeforeWinter)
        {
            if (hoursBeforeWinter < 1 || hoursBeforeWinter > 5 * GenDate.HoursPerDay)
                return Refuse("hoursBeforeWinter must be between 1 and " + 5 * GenDate.HoursPerDay + ".");
            if (Find.TickManager.gameStartAbsTick <= 0) return Refuse("The game has no absolute tick origin yet.");
            var longLat = Find.WorldGrid.LongLatOf(map.Tile);
            var abs = Find.TickManager.TicksAbs;
            Func<int, Season> season = h => GenDate.Season(abs + (long)h * GenDate.TicksPerHour, longLat);
            // The first hour of the first winter twelfth at least
            // hoursBeforeWinter hours on, within two years of now.
            var boundary = -1;
            var hours = 2 * GenDate.DaysPerYear * GenDate.HoursPerDay;
            for (var h = hoursBeforeWinter; h <= hours && boundary < 0; h++)
                if (season(h) == Season.Winter && season(h - 1) == Season.Fall) boundary = h;
            if (boundary < 0) return Refuse("The tile has no autumn-to-winter boundary within two years of now.");
            var shift = (long)(boundary - hoursBeforeWinter) * GenDate.TicksPerHour;
            Find.TickManager.gameStartAbsTick += (int)shift;
            Find.World.tileTemperatures.ClearCaches();
            if (GenDate.Season(Find.TickManager.TicksAbs, longLat) != Season.Fall)
                return Refuse("Calendar landed outside autumn.");
            // Nobody owns winter wear yet: worn or loose parkas, jackets and
            // tuques go, so the lookahead has to produce them.
            var winterDefs = new[] { "Apparel_Parka", "Apparel_Jacket", "Apparel_Tuque" };
            foreach (var p in people)
                foreach (var a in p.apparel.WornApparel.Where(a => winterDefs.Contains(a.def.defName)).ToList()) a.Destroy();
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).Where(t => winterDefs.Contains(t.def.defName)).ToList()) t.Destroy();
            Find.ResearchManager.FinishProject(DefDatabase<ResearchProjectDef>.GetNamed("ComplexClothing"), false);
            var hut = FixtureHut.Build(map, 9);
            var bench = FixtureHut.SpawnInside(map, hut, DefDatabase<ThingDef>.GetNamed("HandTailoringBench"));
            var cloth = FixtureHut.DropOutside(map, hut, ThingDefOf.Cloth, 600);
            Tailors(map, people);
            return new {
                success = true, tick = Find.TickManager.TicksGame, ticksAbs = Find.TickManager.TicksAbs, shiftedTicks = shift,
                hoursBeforeWinter, winterTick = Find.TickManager.TicksGame + (long)hoursBeforeWinter * GenDate.TicksPerHour,
                twelfth = GenDate.Twelfth(Find.TickManager.TicksAbs, longLat.x).ToString(),
                outdoorTemperatureC = map.mapTemperature.OutdoorTemp,
                seasonalTemperatureC = GenTemperature.GetTemperatureFromSeasonAtTile(Find.TickManager.TicksAbs, map.Tile),
                bench = bench.GetUniqueLoadID(), cloth, hut = hut.Summary(),
                colonists = people.Select(p => p.GetUniqueLoadID()).ToList(),
            };
        }

        private static object Tainted(Map map, List<Pawn> people)
        {
            var subject = people.First(p => GearUpkeepTools.Available(p) == null);
            subject.jobs.EndCurrentJob(JobCondition.InterruptForced);
            foreach (var a in subject.apparel.WornApparel.Where(a => a.def.apparel.layers.Contains(ApparelLayerDefOf.Shell)).ToList()) a.Destroy();
            var def = DefDatabase<ThingDef>.GetNamed("Apparel_Parka");
            var tainted = (Apparel)ThingMaker.MakeThing(def, ThingDefOf.Cloth);
            tainted.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Colony);
            tainted.Notify_PawnKilled();
            if (!tainted.WornByCorpse) return Refuse("The parka did not become tainted.");
            var clean = (Apparel)ThingMaker.MakeThing(def, ThingDefOf.Cloth);
            clean.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
            var cells = GenRadial.RadialCellsAround(subject.Position, 6, false)
                .Where(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && c.GetFirstItem(map) == null).Take(2).ToList();
            if (cells.Count < 2) return Refuse("No two free cells beside the subject for the parkas.");
            GenSpawn.Spawn(tainted, cells[0], map); tainted.SetForbidden(false, false);
            GenSpawn.Spawn(clean, cells[1], map); clean.SetForbidden(false, false);
            // Vanilla's optimizer would settle the choice on its own; hold
            // it off so the policy the controller assigns is what decides.
            foreach (var p in people) p.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame + 600000;
            return new {
                success = true, tick = Find.TickManager.TicksGame, pawn = subject.GetUniqueLoadID(),
                tainted = tainted.GetUniqueLoadID(), clean = clean.GetUniqueLoadID(), definition = def.defName,
                policy = subject.outfits.CurrentApparelPolicy.label,
                allowsTainted = subject.outfits.CurrentApparelPolicy.filter.Allows(SpecialThingFilterDefOf.AllowDeadmansApparel),
                colonists = people.Select(p => p.GetUniqueLoadID()).ToList(),
            };
        }

        private static object Soldier(Map map, List<Pawn> people)
        {
            var soldiers = people.Where(p => GearUpkeepTools.Available(p) == null && !p.WorkTagIsDisabled(WorkTags.Violent)
                && !p.WorkTagIsDisabled(WorkTags.Shooting) && p.skills != null && !p.skills.GetSkill(SkillDefOf.Shooting).TotallyDisabled)
                .OrderBy(p => p.thingIDNumber).Take(2).ToList();
            if (soldiers.Count < 2) return Refuse("Fewer than two colonists can shoot.");
            var levels = new[] { 12, 8 };
            for (var i = 0; i < 2; i++) {
                var p = soldiers[i];
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                p.skills.GetSkill(SkillDefOf.Shooting).Level = levels[i];
                p.skills.GetSkill(SkillDefOf.Crafting).Level = 10;
                p.equipment.DestroyAllEquipment();
                foreach (var a in p.apparel.WornApparel.Where(a => a.def.apparel.layers.Contains(ApparelLayerDefOf.Middle)
                    || a.def.apparel.layers.Contains(ApparelLayerDefOf.Overhead)).ToList()) a.Destroy();
            }
            foreach (var name in new[] { "Smithing", "ComplexClothing", "Electricity", "Machining", "FlakArmor" }) {
                var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
                if (project != null) Find.ResearchManager.FinishProject(project, false);
            }
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).Where(t => !(t is Pawn)).ToList()) t.Destroy();
            var hut = FixtureHut.Build(map, 11);
            var smithy = FixtureHut.SpawnInside(map, hut, DefDatabase<ThingDef>.GetNamed("FueledSmithy"));
            smithy.TryGetComp<CompRefuelable>()?.Refuel(500);
            var generator = FixtureHut.SpawnInside(map, hut, DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"));
            generator.TryGetComp<CompRefuelable>()?.Refuel(500);
            var machining = FixtureHut.SpawnInside(map, hut, DefDatabase<ThingDef>.GetNamed("TableMachining"));
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            var steel = FixtureHut.DropOutside(map, hut, ThingDefOf.Steel, 600);
            var cloth = FixtureHut.DropOutside(map, hut, ThingDefOf.Cloth, 150);
            var components = FixtureHut.DropOutside(map, hut, ThingDefOf.ComponentIndustrial, 10);
            var plasteel = FixtureHut.DropOutside(map, hut, ThingDefOf.Plasteel, 40);
            var wood = FixtureHut.DropOutside(map, hut, ThingDefOf.WoodLog, 200);
            var weapons = new List<object>();
            foreach (var name in new[] { "Gun_BoltActionRifle", "Gun_PumpShotgun" }) {
                var weapon = (ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(name));
                weapon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
                if (!GenPlace.TryPlaceThing(weapon, hut.Door + IntVec3.East * 4, map, ThingPlaceMode.Near)) return Refuse("No drop site for " + name);
                weapon.SetForbidden(false, false);
                weapons.Add(new { thingId = weapon.GetUniqueLoadID(), defName = name });
            }
            Tailors(map, people);
            foreach (var p in people) {
                foreach (var name in new[] { "Smithing", "Crafting" }) {
                    var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
                    if (work != null && !p.WorkTypeIsDisabled(work)) p.workSettings.SetPriority(work, 1);
                }
                p.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame + 600000;
            }
            return new {
                success = true, tick = Find.TickManager.TicksGame,
                soldiers = soldiers.Select((p, i) => new { pawn = p.GetUniqueLoadID(), shooting = levels[i] }).ToList(),
                weapons, smithy = smithy.GetUniqueLoadID(), machining = machining.GetUniqueLoadID(), generator = generator.GetUniqueLoadID(),
                powered = machining.TryGetComp<CompPowerTrader>()?.PowerOn ?? false,
                steel, cloth, components, plasteel, wood, hut = hut.Summary(),
                colonists = people.Select(p => p.GetUniqueLoadID()).ToList(),
            };
        }

        private static object Roster(Map map, List<Pawn> people)
        {
            var anchor = people[0].Position;
            var added = new List<string>();
            while (people.Count < RosterSize) {
                var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, colonistRelationChanceFactor: 0f, allowDead: false, allowDowned: false,
                    canGeneratePawnRelations: false, mustBeCapableOfViolence: false, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f));
                var cell = GenRadial.RadialCellsAround(anchor, 8, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && c.GetFirstPawn(map) == null);
                if (cell == default) return Refuse("No free cell near the colonists for a generated pawn.");
                GenSpawn.Spawn(pawn, cell, map);
                if (pawn.needs?.food != null) pawn.needs.food.CurLevelPercentage = .9f;
                if (pawn.needs?.rest != null) pawn.needs.rest.CurLevelPercentage = .9f;
                added.Add(pawn.GetUniqueLoadID());
                people = Colonists(map);
            }
            // Every colonist loses shirt and headgear; a stockpile beside
            // them holds one shirt and one tuque per pawn plus spares, so
            // the recovery is a dressing wave from storage, not a bill run.
            var stripped = new List<object>();
            foreach (var p in people) {
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                var gone = p.apparel.WornApparel.Where(a => a.def.apparel.layers.Contains(ApparelLayerDefOf.Overhead)
                    || a.def.apparel.layers.Contains(ApparelLayerDefOf.OnSkin) && a.def.apparel.bodyPartGroups.Any(g => g == BodyPartGroupDefOf.Torso)).ToList();
                foreach (var a in gone) a.Destroy();
                foreach (var a in p.apparel.WornApparel) p.outfits.forcedHandler.SetForced(a, false);
                stripped.Add(new { pawn = p.GetUniqueLoadID(), removed = gone.Count });
            }
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).ToList()) t.Destroy();
            var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
            var walker = people.First(p => !p.Downed);
            var side = 6;
            var origin = GenRadial.RadialCellsAround(center, 16, true).FirstOrDefault(c =>
                new CellRect(c.x, c.z, side, side).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                    && cell.GetEdifice(map) == null && cell.GetZone(map) == null && cell.Standable(map) && !cell.GetTerrain(map).IsWater
                    && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building
                        || t.def.category == ThingCategory.Item || t is Blueprint || t is Frame))
                && walker.CanReach(c, PathEndMode.Touch, Danger.None));
            if (origin == default) return Refuse("No open reachable " + side + "x" + side + " area near the colonist centroid for the spares.");
            var rect = new CellRect(origin.x, origin.z, side, side);
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var cell in rect.Cells) {
                foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                zone.AddCell(cell);
                map.areaManager.Home[cell] = true;
            }
            var cells = rect.Cells.GetEnumerator();
            var placed = new List<object>();
            foreach (var name in new[] { "Apparel_BasicShirt", "Apparel_Tuque" })
                for (var i = 0; i < people.Count + 2; i++) {
                    if (!cells.MoveNext()) return Refuse("Fixture stockpile ran out of cells.");
                    var def = DefDatabase<ThingDef>.GetNamed(name);
                    var item = (Apparel)ThingMaker.MakeThing(def, ThingDefOf.Cloth);
                    item.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
                    GenSpawn.Spawn(item, cells.Current, map);
                    item.SetForbidden(false, false);
                    placed.Add(new { thingId = item.GetUniqueLoadID(), defName = name });
                }
            foreach (var p in people) p.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame + 600000;
            return new {
                success = true, tick = Find.TickManager.TicksGame, colonists = people.Select(p => p.GetUniqueLoadID()).ToList(),
                added, stripped, zoneId = zone.ID, placed,
                rect = new { minX = rect.minX, minZ = rect.minZ, maxX = rect.maxX, maxZ = rect.maxZ },
            };
        }

        private static List<Pawn> Colonists(Map map) =>
            map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && p.apparel != null && p.outfits != null).OrderBy(p => p.thingIDNumber).ToList();

        private static void Tailors(Map map, List<Pawn> people)
        {
            var tailoring = DefDatabase<WorkTypeDef>.GetNamed("Tailoring");
            foreach (var p in people)
                if (!p.WorkTypeIsDisabled(tailoring)) p.workSettings.SetPriority(tailoring, 1);
        }

        // Slot is the apparel conflict key: two definitions sharing a layer
        // and a body-part group cannot be worn together.
        private static string Slot(ThingDef def) =>
            string.Join("+", def.apparel.layers.Select(l => l.defName).OrderBy(l => l)) + "|" +
            string.Join("+", def.apparel.bodyPartGroups.Select(g => g.defName).OrderBy(g => g));

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
