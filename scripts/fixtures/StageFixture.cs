using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // The Rand states of the combat lab: every staging runs under the pinned state, and test/lab_stage reseed sets the
    // game's seed from a run seed after it, so a run seed is one RNG start.
    public static class StageSeed
    {
        public const int Pinned = 854;

        public static int After(int seed) => unchecked(Pinned ^ (seed * 31337));
    }

    // Applies a StageSpec (#3393) with the FixtureKit primitives, in the order the spec documents, under one pushed
    // Rand state so the same spec on the same map stages the same pawns. The draw order is the combat lab's
    // (#854): its committed metrics baselines assume generation, skills, spawn, gear, wounds and the arrival in that
    // order.
    public static class Stager
    {
        public static StageReply Apply(Map map, StageSpec spec)
        {
            var origin = IntVec3.Zero;
            StageRect? site = null;
            var rooms = new Dictionary<string, StagedRoom>();
            var buildings = new Dictionary<string, StagedThings>();
            var tables = new Dictionary<string, List<Thing>>();
            var bills = new Dictionary<string, string>();
            var things = new Dictionary<string, StagedThings>();
            var plants = new Dictionary<string, StagedThings>();
            var pawns = new Dictionary<string, StagedPawn>();
            var zones = new Dictionary<string, string>();
            var staged = new List<(string Name, Pawn Pawn)>();
            var raiders = new List<Pawn>();
            var prisoners = new List<Pawn>();
            var insects = new List<Pawn>();
            var mechs = new List<Pawn>();
            Faction? hostile = null;
            Faction? Owner(StageFaction f) => f switch
            {
                StageFaction.Player => Faction.OfPlayer,
                StageFaction.Hostile => hostile ??= FixtureKit.HostileFaction(),
                StageFaction.Insect => Faction.OfInsects,
                StageFaction.Mechanoid => Faction.OfMechanoids,
                StageFaction.Ancients => Faction.OfAncients,
                _ => null,
            };
            IntVec3 At(StageCell c) => FixtureKit.InBounds(map, new IntVec3(origin.x + c.X, 0, origin.z + c.Z));
            CellRect Rect(StageRect r)
            {
                var rect = new CellRect(origin.x + r.X, origin.z + r.Z, r.Width, r.Height);
                return r.Width > 0 && r.Height > 0 && rect.InBounds(map) ? rect : throw new ArgumentException($"Rect {r} is empty or off the map.");
            }

            var research = new Dictionary<string, float>();
            foreach (var r in spec.Research)
            {
                var project = FixtureKit.Def<ResearchProjectDef>(r.Def);
                if (r.Progress is float fraction) FixtureKit.SetResearchProgress(project, fraction);
                else FixtureKit.FinishResearch(project);
                research[project.defName] = project.IsFinished ? 1f : project.ProgressPercent;
            }
            if (spec.World?.OutdoorTemperature is float outdoor) FixtureKit.PinOutdoorTemperature(outdoor);
            if (spec.World?.GrowingClimate == true) FixtureKit.PinGrowingClimate();
            if (spec.World?.Hour is int hour) FixtureKit.AdvanceToHour(map, hour);
            if (spec.World?.Storyteller is string storyteller) Find.Storyteller.def = FixtureKit.Def<StorytellerDef>(storyteller);
            if (spec.World?.DaysPassed is float days) FixtureKit.AdvanceToDaysPassed(days);

            Rand.PushState(StageSeed.Pinned);
            try
            {
                if (spec.Site is StageSite s)
                {
                    var anchor = s.Near == StageAnchor.Colonist ? FixtureKit.FirstFreeColonist(map).Position : map.Center;
                    var rect = FixtureKit.FindSite(map, anchor + new IntVec3(s.OffsetX, 0, s.OffsetZ), s.Width, s.Height, s.Radius, c => s.Require.All(g => Ground(map, c, g)));
                    origin = new IntVec3(rect.minX, 0, rect.minZ);
                    site = new StageRect(rect.minX, rect.minZ, rect.Width, rect.Height);
                }
                foreach (var clear in spec.Clear)
                {
                    var k = clear.Kinds;
                    FixtureKit.Clear(map, Rect(clear.Rect), k.Contains(StageClearKind.Things), k.Contains(StageClearKind.Pawns), k.Contains(StageClearKind.Zones),
                        k.Contains(StageClearKind.Roofs), k.Contains(StageClearKind.Fog), k.Contains(StageClearKind.Snow), k.Contains(StageClearKind.Home));
                }
                foreach (var set in spec.Cells)
                {
                    var rect = Rect(set.Rect);
                    var terrain = set.Terrain == null ? null : FixtureKit.Def<TerrainDef>(set.Terrain);
                    var roof = set.Roof == null ? null : FixtureKit.Def<RoofDef>(set.Roof);
                    foreach (var c in rect)
                    {
                        if (terrain != null) FixtureKit.SetTerrain(map, c, terrain);
                        if (roof != null) map.roofGrid.SetRoof(c, roof);
                        if (set.Home is bool home) map.areaManager.Home[c] = home;
                        if (set.Fogged == false) map.fogGrid.Unfog(c);
                    }
                    if (set.Fogged == true) map.fogGrid.Refog(rect);
                }
                foreach (var r in spec.Rooms)
                {
                    var built = FixtureKit.BuildRoom(map, Rect(r.Rect), FixtureKit.Def<ThingDef>(r.Stuff), r.Doors.Select(At).ToList(), r.Roof);
                    Add(rooms, r.Name, new StagedRoom(Ids(built.Walls), Ids(built.Doors), built.Interior.Select(Out).ToList()));
                }
                foreach (var b in spec.Buildings)
                {
                    var def = FixtureKit.Def<ThingDef>(b.Def);
                    if (def.category != ThingCategory.Building) throw new ArgumentException($"{b.Def} is not a building.");
                    var stuff = FixtureKit.Stuff(def, b.Stuff);
                    var rot = Rot4.North;
                    var made = new List<Thing>();
                    foreach (var cell in b.Cells.Select(At))
                    {
                        Thing thing;
                        if (b.State == StageBuildState.Blueprint)
                            thing = GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Owner(b.Faction) ?? Faction.OfPlayer, stuff);
                        else
                        {
                            thing = ThingMaker.MakeThing(b.State == StageBuildState.Frame ? def.frameDef : def, stuff);
                            // A hive is the insects' whatever the spec says.
                            var owner = thing is Hive ? Faction.OfInsects : Owner(b.Faction);
                            if (owner != null && thing.def.CanHaveFaction) thing.SetFaction(owner);
                            if (b.HitPoints > 0 && b.State == StageBuildState.Complete && def.useHitPoints) thing.HitPoints = Math.Max(1, Math.Min(b.HitPoints, thing.MaxHitPoints));
                            thing = GenSpawn.Spawn(thing, cell, map, rot);
                        }
                        if (b.Fuel is float fuel)
                        {
                            var refuelable = b.State == StageBuildState.Complete ? thing.TryGetComp<CompRefuelable>() : null;
                            if (refuelable == null) throw new ArgumentException($"Building {b.Name}: only a finished refuelable building takes fuel.");
                            refuelable.Refuel(Math.Max(0f, Math.Min(fuel, refuelable.Props.fuelCapacity) - refuelable.Fuel));
                        }
                        if (b.ForPrisoners)
                        {
                            if (b.State != StageBuildState.Complete || !(thing is Building_Bed bed) || !bed.def.building.bed_humanlike) throw new ArgumentException($"Building {b.Name}: only a finished humanlike bed is for prisoners.");
                            bed.ForPrisoners = true;
                            // Setting the flag alone leaves a paused room outside native prison custody.
                            bed.GetDistrict()?.Notify_RoomShapeOrContainedBedsChanged();
                            bed.GetRoom()?.Notify_RoomShapeChanged();
                        }
                        made.Add(thing);
                    }
                    Add(buildings, b.Name, new StagedThings(Ids(made), made.Select(t => Out(t.Position)).ToList()) { Guns = Guns(made) });
                    tables[b.Name] = made;
                }
                foreach (var b in spec.Bills)
                {
                    var table = tables.TryGetValue(b.Building, out var named) && named.Count == 1 ? named[0] as Building_WorkTable : null;
                    if (table == null) throw new ArgumentException($"Bill {b.Name}: {b.Building} is not one staged work table.");
                    var recipe = FixtureKit.Def<RecipeDef>(b.Recipe);
                    if (!table.def.AllRecipes.Contains(recipe)) throw new ArgumentException($"Bill {b.Name}: {table.def.defName} does not offer {recipe.defName}.");
                    if (b.RepeatCount < 1) throw new ArgumentException($"Bill {b.Name}: repeat count {b.RepeatCount} is below 1.");
                    var bill = recipe.MakeNewBill() as Bill_Production ?? throw new ArgumentException($"Bill {b.Name}: {recipe.defName} is not a production recipe.");
                    bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
                    bill.repeatCount = b.RepeatCount;
                    if (b.DropOnFloor) bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor, null);
                    table.BillStack.AddBill(bill);
                    Add(bills, b.Name, bill.GetUniqueLoadID());
                }
                foreach (var t in spec.Things)
                {
                    var def = FixtureKit.Def<ThingDef>(t.Def);
                    if (def.category != ThingCategory.Item) throw new ArgumentException($"{t.Def} is not an item.");
                    if (t.Count < 1 || t.Count > 1000000 || (!t.Near && t.Count > def.stackLimit)) throw new ArgumentException($"Thing {t.Name}: count {t.Count} is out of range.");
                    var stuff = FixtureKit.Stuff(def, t.Stuff);
                    var cell = At(t.Cell);
                    var made = new List<Thing>();
                    for (var placed = 0; placed < t.Count;)
                    {
                        var thing = ThingMaker.MakeThing(def, stuff);
                        thing.stackCount = Math.Min(t.Count - placed, def.stackLimit);
                        placed += thing.stackCount;
                        if (!t.Near) thing = GenSpawn.Spawn(thing, cell, map, Rot4.North);
                        else if (!GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near, out thing)) throw new InvalidOperationException($"No room near {cell.x},{cell.z} for {t.Name}.");
                        if (t.Forbidden) thing.SetForbidden(true, false);
                        if (!made.Contains(thing)) made.Add(thing);
                    }
                    Add(things, t.Name, new StagedThings(Ids(made), made.Select(x => Out(x.Position)).ToList()));
                }
                foreach (var p in spec.Plants)
                {
                    var def = FixtureKit.Def<ThingDef>(p.Def);
                    if (def.plant == null) throw new ArgumentException($"{p.Def} is not a plant.");
                    if (p.Growth < 0f || p.Growth > 1f) throw new ArgumentException($"Plant {p.Name}: growth {p.Growth} is outside 0..1.");
                    var made = new List<Thing>();
                    foreach (var cell in p.Cells.Select(At))
                    {
                        foreach (var old in cell.GetThingList(map).OfType<Plant>().ToList()) old.Destroy();
                        var plant = (Plant)GenSpawn.Spawn(def, cell, map);
                        plant.Growth = p.Growth;
                        plant.sown = p.Sown;
                        made.Add(plant);
                    }
                    Add(plants, p.Name, new StagedThings(Ids(made), made.Select(x => Out(x.Position)).ToList()));
                }

                var colonists = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                var arrivalName = spec.Raid?.Arrival;
                var arrival = string.IsNullOrEmpty(arrivalName) ? null : FixtureKit.Def<PawnsArrivalModeDef>(arrivalName);
                IntVec3? arrivalCenter = null;
                foreach (var p in spec.Pawns)
                {
                    IntVec3? at = p.Cell is StageCell c ? At(c) : (IntVec3?)null;
                    var pawn = p.Colonist is int index ? Move(colonists, index, at, p)
                        : Generate(map, p, Owner(p.Faction), at ?? throw new ArgumentException($"Pawn {p.Name}: a generated pawn needs a cell."), arrival != null, raiders, prisoners, insects, mechs);
                    var cell = at ?? pawn.Position;
                    Skills(pawn, p);
                    Equip(pawn, p);
                    Set(pawn, p);
                    if (p.Bed != null) Claim(pawn, p, tables);
                    if (p.MoodTarget is float target) HeldMood.Hold(pawn, target);
                    if (staged.Any(x => x.Name == p.Name)) throw new ArgumentException($"Two pawns are named {p.Name}.");
                    staged.Add((p.Name, pawn));
                    if (arrival != null && pawn.Faction != Faction.OfPlayer && arrivalCenter == null) arrivalCenter = cell;
                }
                if (arrival != null && raiders.Count > 0)
                    arrival.Worker.Arrive(raiders, new IncidentParms { target = map, faction = hostile, raidArrivalMode = arrival, spawnCenter = arrivalCenter!.Value, spawnRotation = Rot4.South, points = 500f });
            }
            finally
            {
                Rand.PopState();
            }
            // A siege is a real siege lord camped at the spot: the game's own LordToil_Siege places the blueprints, drops
            // the supplies and sets the builders to work on the frames.
            if (raiders.Count > 0 && spec.Raid?.Siege is StageSiege siege)
                LordMaker.MakeNewLord(raiders[0].Faction, new LordJob_Siege(raiders[0].Faction, At(siege.Camp), siege.Points), map, raiders);
            else if (raiders.Count > 0)
                LordMaker.MakeNewLord(raiders[0].Faction, new LordJob_AssaultColony(raiders[0].Faction, canKidnap: false, canTimeoutOrFlee: false, sappers: spec.Raid?.Sappers == true, canSteal: false), map, raiders);
            if (insects.Count > 0)
                LordMaker.MakeNewLord(Faction.OfInsects, new LordJob_AssaultColony(Faction.OfInsects, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, insects);
            if (mechs.Count > 0)
                LordMaker.MakeNewLord(Faction.OfMechanoids, new LordJob_AssaultColony(Faction.OfMechanoids, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, mechs);
            if (spec.Raid?.PrisonBreak == true)
            {
                if (prisoners.Count == 0) throw new ArgumentException("A prison break needs a prisoner.");
                PrisonBreakUtility.StartPrisonBreak(prisoners[0], out _, out _, out _, out var escaping);
                if (escaping == null || escaping.Count == 0) throw new InvalidOperationException("The prison break freed no prisoner.");
            }
            foreach (var (name, pawn) in staged)
                pawns[name] = new StagedPawn
                {
                    Id = pawn.GetUniqueLoadID(), Kind = pawn.kindDef.defName, Cell = Out(pawn.PositionHeld), Spawned = pawn.Spawned,
                    Hostile = pawn.HostileTo(Faction.OfPlayer), Prisoner = pawn.IsPrisonerOfColony, Weapon = pawn.equipment?.Primary?.def.defName,
                    LordJob = pawn.GetLord()?.LordJob?.GetType().Name, MentalState = pawn.MentalStateDef?.defName, Inspiration = pawn.InspirationDef?.defName,
                    Health = pawn.health.summaryHealth.SummaryHealthPercent,
                    Worn = pawn.apparel?.WornApparel.Count ?? 0,
                    CombatPower = pawn.kindDef.combatPower, MeleeDps = pawn.GetStatValue(StatDefOf.MeleeDPS), RangedDps = NativeDefenseStats.PawnRangedDps(pawn) * NativeDefenseStats.PawnRangedHitFactor(pawn), Primary = Primary(pawn),
                    Skills = spec.Pawns.First(x => x.Name == name).Skills?.Keys.ToDictionary(k => k, k => pawn.skills.GetSkill(FixtureKit.Def<SkillDef>(k)).Level),
                };
            foreach (var z in spec.Zones)
                Add(zones, z.Name, LayZone(map, z, z.Cells.Select(At).ToList()));
            var incidents = new Dictionary<string, StagedIncident>();
            foreach (var i in spec.Incidents)
            {
                var arrived = FixtureKit.FireIncident(map, FixtureKit.Def<IncidentDef>(i.Def), i.TraderKind == null ? null : FixtureKit.Def<TraderKindDef>(i.TraderKind));
                Add(incidents, i.Name, new StagedIncident(Ids(arrived.Where(x => x.trader?.traderKind != null))));
            }
            var rows = FixtureKit.DigestRows(map);
            return new StageReply { Success = true, Research = research, Site = site, Rooms = rooms, Buildings = buildings, Bills = bills, Things = things, Plants = plants, Pawns = pawns, Zones = zones,
                Incidents = incidents,OutdoorTemperature = map.mapTemperature.OutdoorTemp, Hour = GenLocalDate.HourOfDay(map),
                Digest = FixtureKit.Digest(rows), DigestRows = rows };
        }

        private static bool Ground(Map map, IntVec3 c, StageGround g) => g switch
        {
            StageGround.Standable => c.Standable(map),
            StageGround.Empty => c.GetThingList(map).Count == 0,
            StageGround.Unzoned => c.GetZone(map) == null,
            StageGround.Unfogged => !c.Fogged(map),
            StageGround.Unroofed => !c.Roofed(map),
            StageGround.Heavy => c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy),
            _ => throw new ArgumentException($"Unknown ground {g}."),
        };

        private static Pawn Move(List<Pawn> colonists, int index, IntVec3? cell, StagePawn p)
        {
            if (p.Kind != null) throw new ArgumentException($"Pawn {p.Name} names both a Kind and a Colonist.");
            if (index < 0 || index >= colonists.Count) throw new ArgumentException($"Colonist index {index}: the map has {colonists.Count} free colonists.");
            var pawn = colonists[index];
            if (cell is IntVec3 to)
            {
                pawn.Position = to;
                pawn.Notify_Teleported(true, true);
            }
            return pawn;
        }

        // Spawn pawn: generated from its kind, then traits, bad hediffs, skills and strip before it spawns (a raider
        // under an arrival mode is left for the arrival), then the prisoner, training and manhunter state.
        private static Pawn Generate(Map map, StagePawn p, Faction? faction, IntVec3 cell, bool arrival,
            List<Pawn> raiders, List<Pawn> prisoners, List<Pawn> insects, List<Pawn> mechs)
        {
            var kind = FixtureKit.Def<PawnKindDef>(p.Kind ?? throw new ArgumentException($"Pawn {p.Name} names neither a Kind nor a Colonist."));
            if (p.Manhunter && faction != null) throw new ArgumentException($"Pawn {p.Name}: a manhunter has no faction.");
            if (p.Prisoner && p.Faction == StageFaction.Player) throw new ArgumentException($"Pawn {p.Name}: a prisoner is not the colony's own.");
            var request = kind.RaceProps.Humanlike
                ? new PawnGenerationRequest(kind, faction, forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: true, allowAddictions: false,
                    fixedBiologicalAge: p.Age, fixedChronologicalAge: p.Age, forceBaselinerChance: 1f, developmentalStages: DevelopmentalStage.Adult)
                : new PawnGenerationRequest(kind, faction, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                    fixedBiologicalAge: p.Age, fixedChronologicalAge: p.Age, developmentalStages: DevelopmentalStage.Adult);
            var pawn = PawnGenerator.GeneratePawn(request);
            if (p.ClearTraits) FixtureKit.RemoveTraits(pawn);
            FixtureKit.RemoveBadHediffs(pawn);
            if (p.Gear?.Strip == true)
            {
                pawn.apparel?.DestroyAll();
                pawn.inventory?.DestroyAll();
            }
            var raider = p.Faction == StageFaction.Hostile && !p.Prisoner;
            if (!(raider && arrival)) GenSpawn.Spawn(pawn, cell, map);
            if (p.Prisoner)
            {
                pawn.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                prisoners.Add(pawn);
            }
            else if (raider) raiders.Add(pawn);
            else if (p.Faction == StageFaction.Insect) insects.Add(pawn);
            else if (p.Faction == StageFaction.Mechanoid) mechs.Add(pawn);
            foreach (var name in p.Trained) pawn.training.Train(FixtureKit.Def<TrainableDef>(name), null, complete: true);
            if (p.Manhunter && !pawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true))
                throw new InvalidOperationException($"{kind.defName} did not go manhunter.");
            return pawn;
        }

        // Every skill at the staged level without passion, for a generated pawn and a moved colonist alike.
        private static void Skills(Pawn pawn, StagePawn p)
        {
            if (p.SkillLevel is int level)
                foreach (var skill in pawn.skills.skills)
                {
                    skill.Level = level;
                    skill.passion = Passion.None;
                }
            foreach (var pair in p.Skills ?? new Dictionary<string, int>())
            {
                var skill = pawn.skills.GetSkill(FixtureKit.Def<SkillDef>(pair.Key));
                skill.Level = pair.Value;
                skill.passion = Passion.None;
            }
        }

        // Gear and condition, in the staging's draw order: weapon, apparel, anesthetic, hediffs, the blunt blow,
        // inventory, stun.
        private static void Equip(Pawn pawn, StagePawn p)
        {
            if (p.Gear != null && pawn.equipment != null) FixtureKit.Arm(pawn, p.Gear.Weapon, p.Gear.WeaponStuff);
            foreach (var name in p.Gear?.Apparel ?? new List<string>())
            {
                var def = FixtureKit.Def<ThingDef>(name);
                var worn = (Apparel)ThingMaker.MakeThing(def, FixtureKit.Stuff(def, p.Gear?.ApparelStuff ?? ""));
                pawn.apparel.Wear(worn, false, true);
                // A new shield starts empty; stage it fully charged.
                if (worn.GetComp<CompShield>() is CompShield shield)
                    Traverse.Create(shield).Field("energy").SetValue(worn.GetStatValue(StatDefOf.EnergyShieldEnergyMax));
            }
            if (p.Downed) pawn.health.AddHediff(HediffDefOf.Anesthetic);
            foreach (var name in p.Hediffs) pawn.health.AddHediff(FixtureKit.Def<HediffDef>(name));
            if (p.Injured) pawn.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 8f));
            foreach (var name in p.Gear?.Inventory ?? new List<string>())
            {
                var def = FixtureKit.Def<ThingDef>(name);
                pawn.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(def, FixtureKit.Stuff(def, "")));
            }
            if (p.StunTicks > 0) pawn.stances.stunner.StunFor(p.StunTicks, null, addBattleLog: false);
        }

        // Set pawn: work, timetable and needs.
        private static void Set(Pawn pawn, StagePawn p)
        {
            if (p.AllWork is int all) FixtureKit.SetAllWork(pawn, Priority(all));
            foreach (var pair in p.Work ?? new Dictionary<string, int>()) FixtureKit.SetWork(pawn, FixtureKit.Def<WorkTypeDef>(pair.Key), Priority(pair.Value));
            if (p.Timetable is StageTimeAssignment hours)
                FixtureKit.SetTimetable(pawn, hours switch
                {
                    StageTimeAssignment.Anything => TimeAssignmentDefOf.Anything,
                    StageTimeAssignment.Work => TimeAssignmentDefOf.Work,
                    StageTimeAssignment.Joy => TimeAssignmentDefOf.Joy,
                    StageTimeAssignment.Sleep => TimeAssignmentDefOf.Sleep,
                    _ => TimeAssignmentDefOf.Meditate,
                });
            foreach (var pair in p.Needs ?? new Dictionary<string, float>())
            {
                var need = pawn.needs?.TryGetNeed(FixtureKit.Def<NeedDef>(pair.Key)) ?? throw new ArgumentException($"Pawn {p.Name} has no {pair.Key} need.");
                need.CurLevelPercentage = Math.Max(0f, Math.Min(1f, pair.Value));
            }
            if (p.MentalState != null)
            {
                var state = FixtureKit.Def<MentalStateDef>(p.MentalState);
                if (pawn.drafter != null) pawn.drafter.Drafted = false;
                if (!pawn.mindState.mentalStateHandler.TryStartMentalState(state, forced: true, forceWake: true) || pawn.MentalStateDef != state)
                    throw new InvalidOperationException($"Pawn {p.Name}: {state.defName} did not start.");
            }
            if (p.Inspiration != null)
            {
                var inspiration = FixtureKit.Def<InspirationDef>(p.Inspiration);
                if (pawn.mindState?.inspirationHandler == null || !pawn.mindState.inspirationHandler.TryStartInspiration(inspiration) || pawn.InspirationDef != inspiration)
                    throw new InvalidOperationException($"Pawn {p.Name}: {inspiration.defName} did not start.");
            }
        }

        // Claim bed: the pawn owns the first bed of the named staged building.
        private static void Claim(Pawn pawn, StagePawn p, Dictionary<string, List<Thing>> built)
        {
            if (!built.TryGetValue(p.Bed ?? throw new ArgumentException($"Pawn {p.Name}: bed is required."), out var made) || !(made.FirstOrDefault() is Building_Bed bed)) throw new ArgumentException($"Pawn {p.Name}: {p.Bed} is not a staged bed.");
            if (pawn.ownership == null || !pawn.ownership.ClaimBedIfNonMedical(bed) || pawn.ownership.OwnedBed != bed)
                throw new InvalidOperationException($"Pawn {p.Name} could not claim {p.Bed}.");
        }

        private static int Priority(int value) => value >= 0 && value <= 4 ? value : throw new ArgumentException($"Work priority {value} is not within 0..4.");

        private static string LayZone(Map map, StageZone z, List<IntVec3> cells)
        {
            if (cells.Count == 0) throw new ArgumentException($"Zone {z.Name} has no cells.");
            var stockpile = z.Kind == StageZoneKind.Stockpile;
            if (stockpile ? z.Plant != null : z.Priority != null || z.Allow != null) throw new ArgumentException($"Zone {z.Name}: a field does not fit a {z.Kind} zone.");
            Zone zone = stockpile ? new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager) : (Zone)new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            if (z.Label != null) zone.label = z.Label;
            foreach (var c in cells)
            {
                if (c.GetZone(map) != null) throw new ArgumentException($"Zone {z.Name}: cell {c.x},{c.z} is already zoned.");
                zone.AddCell(c);
            }
            if (zone is Zone_Stockpile pile)
            {
                if (z.Priority is StageStoragePriority priority) pile.settings.Priority = (StoragePriority)Enum.Parse(typeof(StoragePriority), priority.ToString());
                if (z.Allow != null)
                {
                    pile.settings.filter.SetDisallowAll();
                    foreach (var name in z.Allow) pile.settings.filter.SetAllow(FixtureKit.Def<ThingDef>(name), true);
                }
            }
            else if (zone is Zone_Growing field && z.Plant != null) field.SetPlantDefToGrow(FixtureKit.Def<ThingDef>(z.Plant));
            return zone.GetUniqueLoadID();
        }

        private static void Add<T>(Dictionary<string, T> made, string name, T value)
        {
            if (string.IsNullOrEmpty(name) || made.ContainsKey(name)) throw new ArgumentException($"Every staged entry needs its own name; \"{name}\" is empty or taken.");
            made[name] = value;
        }

        private static List<string> Ids(IEnumerable<Thing> made) => made.Select(t => t.GetUniqueLoadID()).ToList();

        // The colony's own measure of a turret (NativeColonyObservationTools): a player power trader with a gun, its damage per
        // second and whether the trader is on.
        private static List<StagedGun> Guns(IEnumerable<Thing> made) => made
            .OfType<Building>()
            .Where(b => b.Faction == Faction.OfPlayer && b.TryGetComp<CompPowerTrader>() != null)
            .Select(b => (Building: b, Dps: NativeDefenseStats.TurretDps(b)))
            .Where(g => g.Dps.HasValue)
            .Select(g => new StagedGun(g.Building.GetUniqueLoadID(), (g.Dps ?? throw new InvalidOperationException("Gun DPS missing.")), g.Building.TryGetComp<CompPowerTrader>().PowerOn))
            .ToList();

        // The primary weapon's kind, as NativeDefenseStats.PawnRangedDps reads it.
        private static StagedPrimary Primary(Pawn pawn)
        {
            if (pawn.equipment?.Primary == null) return StagedPrimary.None;
            var verb = pawn.equipment.PrimaryEq?.PrimaryVerb;
            return verb != null && !verb.IsMeleeAttack ? StagedPrimary.Ranged : StagedPrimary.Melee;
        }

        private static StageCell Out(IntVec3 c) => new StageCell(c.x, c.z);
    }

    public sealed class StageFixture
    {
        [Tool("test/stage", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#3393, #3395): stage a StageSpec (RimGovernor.Fixtures.Contracts) on the current game in one call: finish or set research, pin the outdoor temperature and move the clock, find a site, clear areas, set cells, build rooms, place buildings, add production bills to them, spawn items, spawn plants at a growth, spawn or set pawns (generated or existing colonists, gear, wounds, work, timetable, needs, mental state, inspiration) with a raid's arrival and lords, lay zones and fire incidents. Resets the lab damage and shot ledgers. Replies a StageReply: the ids and cells of everything made, keyed by the spec's names, and a name-free digest.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "The StageSpec as JSON (FixtureJson).")] string spec)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var parsed = FixtureJson.Deserialize<StageSpec>(spec ?? "");
                var map = parsed.Paused ? FixtureKit.PausedMap() : FixtureKit.CurrentMap();
                LabDamageLedger.Reset();
                LabShotLedger.Reset();
                return FixtureKit.Reply(Stager.Apply(map, parsed));
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
