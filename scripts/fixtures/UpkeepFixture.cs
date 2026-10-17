using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools
{
    // Disposable scenario preparation; never included in production builds.
    public sealed class UpkeepFixture
    {
        [Tool("test/deconstruct_prepare", Description = "Stage exact non-colony deconstruction targets. Test builds only.")]
        public async Task<object> PrepareDeconstruction(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
                var cells = GenRadial.RadialCellsAround(pawn.Position, 25, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null && !c.Roofed(map)
                    && RoofSupportSafety.GeometryKnown(map, c)
                    && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)).Take(30).ToList();
                if (cells.Count < 30) throw new InvalidOperationException("No open deconstruction fixture site.");
                var ids = new System.Collections.Generic.List<string>();
                for (int i = 0; i < 6; i++) {
                    var wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                    if (i == 0) wall.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(wall, cells[i * 5], map);
                    wall.SetForbidden(false, false);
                    ids.Add(wall.GetUniqueLoadID());
                }
                foreach (var p in map.mapPawns.FreeColonistsSpawned) {
                    foreach (var h in p.health.hediffSet.hediffs.Where(h => h.def.isBad && !(h is Hediff_MissingPart)).ToList()) p.health.RemoveHediff(h);
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                    p.timetable.SetAssignment(GenLocalDate.HourOfDay(p), TimeAssignmentDefOf.Work);
                }
                var player = map.listerThings.AllThings.First(t => t.GetUniqueLoadID() == ids[1]);
                map.designationManager.AddDesignation(new Designation(player, DesignationDefOf.Deconstruct));
                return new { success = true, ids };
            }, cancellationToken);
        }

        // The thing list probe: spawns one of each thing category on a
        // row of seven cells from cell ("x,z") eastward (item, wall, wall
        // blueprint, wall frame, plant, filth, corpse), then a shelf on the
        // next two holding two stacks on its first, and replies the row as
        // a serialized grid (base64 mirror.CellGrid), for the case to decode on
        // the brain side and compare with what was spawned. Also replies the
        // timed capture of the whole map (ms).
        [Tool("test/grid_things", Description = "UNSAFE FOR MODEL EXECUTION. Disposable probe (#2261): spawn one of each thing category on seven cells east of cell (\"x,z\") and a shelf holding two stacks on the next two, and reply that row as a base64 mirror.CellGrid plus the whole-map read cost. Test builds only.")]
        public async Task<object> GridThings(IRimBridgeContext ctx, CancellationToken cancellationToken, string cell)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var p = (cell ?? "").Split(',');
                if (map == null || p.Length != 2 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var z) || !new IntVec3(x + 8, 0, z).InBounds(map) || !new IntVec3(x, 0, z).InBounds(map))
                    throw new ArgumentException("Expected one in-bounds x,z cell with eight cells free to its east on the current map");
                IntVec3 At(int i) => new IntVec3(x + i, 0, z);
                var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = 25;
                GenSpawn.Spawn(steel, At(0), map);
                var wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, At(1), map);
                GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, At(2), map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                var frame = (Frame)ThingMaker.MakeThing(ThingDefOf.Wall.frameDef, ThingDefOf.WoodLog);
                frame.SetFactionDirect(Faction.OfPlayer);
                frame.SetStuffDirect(ThingDefOf.WoodLog);
                GenSpawn.Spawn(frame, At(3), map);
                var plant = (Plant)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Plant_Potato"));
                GenSpawn.Spawn(plant, At(4), map);
                plant.Growth = 0.5f;
                GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Filth_Dirt")), At(5), map);
                var animal = PawnGenerator.GeneratePawn(PawnKindDef.Named("Muffalo"));
                GenSpawn.Spawn(animal, At(6), map);
                animal.Kill(null);
                // A 2x1 shelf on cells 7 and 8 holding two stacks on cell 7 (#3233).
                var shelf = (Building)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Shelf"), ThingDefOf.WoodLog);
                shelf.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(shelf, At(7), map, Rot4.North);
                var shelfSteel = ThingMaker.MakeThing(ThingDefOf.Steel); shelfSteel.stackCount = 10;
                GenSpawn.Spawn(shelfSteel, At(7), map);
                var shelfWood = ThingMaker.MakeThing(ThingDefOf.WoodLog); shelfWood.stackCount = 10;
                GenSpawn.Spawn(shelfWood, At(7), map);
                var row = CellGridReader.Read(map, x, z, 9, 1);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                CellGridReader.Read(map);
                var readMs = watch.Elapsed.TotalMilliseconds;
                return FixtureKit.Reply(new Fx.GridThingsRead { Grid = Convert.ToBase64String(RimGovernor.Contracts.Wire.WireJson.SerializeToUtf8Bytes(row)),
                    FrameId = frame.thingIDNumber, ReadMs = readMs });
            }, cancellationToken);

        [Tool("test/deconstruct_target", Description = "Inspect or mutate one staged deconstruction target. Test builds only.")]
        public async Task<object> DeconstructionTarget(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact target id.")] string target,
            [ToolParameter(Description = "inspect, replace, vanish, or colony.", DefaultValue = "inspect")] string action = "inspect")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var thing = RefIndex.Thing(map, target);
                if (thing != null && action == "replace") {
                    var prior = map.designationManager.DesignationOn(thing, DesignationDefOf.Deconstruct);
                    if (prior != null) map.designationManager.RemoveDesignation(prior);
                    map.designationManager.AddDesignation(new Designation(thing, DesignationDefOf.Deconstruct));
                }
                if (thing != null && action == "vanish") thing.Destroy(DestroyMode.Vanish);
                if (thing != null && action == "colony") thing.SetFaction(Faction.OfPlayer);
                return new { success = true, present = thing != null && thing.Spawned,
                    designated = thing != null && map.designationManager.DesignationOn(thing, DesignationDefOf.Deconstruct) != null };
            }, cancellationToken);
        }

        // Spoilage preference probe: spawns an old and a fresh stack of
        // raw meat and of simple meals near a colonist, the fresh one closer, and
        // reports which stack a bill ingredient pick and a meal pick choose with
        // the preference off, on, and on with the old stack forbidden.
        [Tool("test/spoilage_pick", Description = "UNSAFE FOR MODEL EXECUTION. Disposable probe (#2520): spawn old and fresh meat and meal stacks near a colonist and reply which each vanilla-path pick chooses with the spoilage preference off, on, and on with the old stack forbidden. Test builds only.")]
        public async Task<object> SpoilagePick(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var pawn = map?.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                if (pawn == null) throw new InvalidOperationException("No spawned colonist");
                var free = GenRadial.RadialCellsAround(pawn.Position, 20, true).Where(c => c.InBounds(map) && c.Standable(map)
                    && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).ToList();
                IntVec3 Cell(int minDistance) => free.First(c => (c - pawn.Position).LengthManhattan >= minDistance);
                Thing Stack(ThingDef def, int ticksUntilRot, IntVec3 at) {
                    var thing = ThingMaker.MakeThing(def);
                    thing.stackCount = 5;
                    GenSpawn.Spawn(thing, at, map);
                    var rot = thing.TryGetComp<CompRottable>();
                    rot.RotProgress = rot.PropsRot.TicksToRotStart - ticksUntilRot;
                    return thing;
                }
                var meat = DefDatabase<ThingDef>.GetNamed("Meat_Muffalo");
                var meal = ThingDefOf.MealSimple;
                // The old stacks sit outside vanilla's 30000-tick freshness bonus, further away than the fresh ones.
                var freshMeat = Stack(meat, 100000, Cell(3)); var oldMeat = Stack(meat, 45000, Cell(11));
                var freshMeal = Stack(meal, 200000, Cell(4)); var oldMeal = Stack(meal, 45000, Cell(12));
                foreach (var m in new[] { freshMeat, oldMeat, freshMeal, oldMeal }) m.SetForbidden(false, false);
                pawn.needs.food.CurLevelPercentage = 0.1f;
                var ingredient = new IngredientCount();
                ingredient.filter.SetAllow(ThingCategoryDefOf.MeatRaw, true);
                ingredient.SetBaseCount(1f);
                var ingredients = new List<IngredientCount> { ingredient };
                string Bill() {
                    var chosen = new List<ThingCount>();
                    return WorkGiver_DoBill.TryFindBestFixedIngredients(ingredients, pawn, pawn, chosen) && chosen.Count > 0 ? chosen[0].Thing.GetUniqueLoadID() : "";
                }
                string Meal() {
                    var found = FoodUtility.BestFoodSourceOnMap(pawn, pawn, false, out var _, FoodPreferability.MealLavish, allowPlant: false, allowDrug: false, allowCorpse: false, allowDispenserFull: false, allowDispenserEmpty: false);
                    return found == null ? "" : found.GetUniqueLoadID();
                }
                var previous = SpoilagePreference.Active;
                try {
                    SpoilagePreference.Active = () => false;
                    var off = new Fx.SpoilagePicks { Bill = Bill(), Meal = Meal() };
                    SpoilagePreference.Active = () => true;
                    var on = new Fx.SpoilagePicks { Bill = Bill(), Meal = Meal() };
                    oldMeat.SetForbidden(true, false); oldMeal.SetForbidden(true, false);
                    var forbidden = new Fx.SpoilagePicks { Bill = Bill(), Meal = Meal() };
                    return FixtureKit.Reply(new Fx.SpoilagePickRead { FreshMeat = freshMeat.GetUniqueLoadID(), OldMeat = oldMeat.GetUniqueLoadID(),
                        FreshMeal = freshMeal.GetUniqueLoadID(), OldMeal = oldMeal.GetUniqueLoadID(), Off = off, On = on, Forbidden = forbidden });
                } finally { SpoilagePreference.Active = previous; }
            }, cancellationToken);
    }
}
