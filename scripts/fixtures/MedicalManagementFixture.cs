using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class MedicalManagementFixture
    {
        private static int surgicalId;

        // SurgeryRoom raises a roofed 5x5 wood ring at origin (south-west
        // corner, east door) with sterile tile, a fuelled torch and one medical
        // hospital bed, for prisoners when asked. Returns a free interior cell.
        private static IntVec3 SurgeryRoom(Map map, IntVec3 origin, bool prisoners)
        {
            var rect = new CellRect(origin.x, origin.z, 5, 5);
            var door = new IntVec3(origin.x + 4, 0, origin.z + 2);
            foreach (var c in rect.Cells) {
                if (!c.InBounds(map)) throw new InvalidOperationException("Surgery room runs off the map at " + c);
                foreach (var t in c.GetThingList(map).Where(t => !(t is Pawn)).ToList()) t.Destroy(DestroyMode.Vanish);
                foreach (var p in c.GetThingList(map).OfType<Pawn>().ToList()) { p.Position = CellFinder.StandableCellNear(origin + new IntVec3(-3, 0, 0), map, 5); p.Notify_Teleported(true, true); }
                map.terrainGrid.SetTerrain(c, TerrainDef.Named("SterileTile"));
                if (rect.IsOnEdge(c)) {
                    var wall = ThingMaker.MakeThing(ThingDef.Named(c == door ? "Door" : "Wall"), ThingDefOf.WoodLog);
                    wall.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(wall, c, map);
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            var bed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("HospitalBed"), GenStuff.DefaultStuffFor(ThingDef.Named("HospitalBed")));
            bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, origin + new IntVec3(1, 0, 1), map, Rot4.North);
            if (prisoners) bed.ForPrisoners = true;
            bed.Medical = true;
            var torch = ThingMaker.MakeThing(ThingDef.Named("TorchLamp"), GenStuff.DefaultStuffFor(ThingDef.Named("TorchLamp")));
            torch.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(torch, origin + new IntVec3(3, 0, 3), map);
            torch.TryGetComp<CompRefuelable>()?.Refuel(1000f);
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            return origin + new IntVec3(3, 0, 1);
        }

        // Medical-bill probe: one colonist missing a leg (InstallPegLeg's
        // precondition) with an unrelated bill already on it, and one colony
        // prisoner of a non-player faction, on whom RemoveBodyPart is a violation.
        [Tool("test/surgery_intent_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Remove one colonist's leg, queue an unrelated bill on them, and hold one non-player pawn prisoner. Never installs anything.")]
        public async Task<object> SurgeryIntent(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) return new { success = false, reason = "A paused colony map is required." };
                var patient = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault(p => !p.Dead
                    && !p.health.hediffSet.hediffs.Any(h => h.def == HediffDefOf.MissingBodyPart)
                    && p.health.hediffSet.GetNotMissingParts().Any(part => part.def.defName == "Leg"));
                if (patient == null) return new { success = false, reason = "No colonist with an intact leg." };
                var leg = patient.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == "Leg");
                patient.health.AddHediff(HediffDefOf.MissingBodyPart, leg);
                HealthCardUtility.CreateSurgeryBill(patient, patient.def.AllRecipes.First(r => r.defName == "Anesthetize"), null);
                var faction = Find.FactionManager.AllFactionsListForReading.First(f => !f.IsPlayer && !f.Hidden && f.def.humanlikeFaction);
                var prisoner = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, faction, forceGenerateNewPawn: true,
                    canGeneratePawnRelations: false, developmentalStages: DevelopmentalStage.Adult));
                GenSpawn.Spawn(prisoner, CellFinder.StandableCellNear(patient.Position, map, 8), map);
                prisoner.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                var kidney = prisoner.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == "Kidney");
                // a colonist who can doctor, for the surgeon restriction.
                var surgeon = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .FirstOrDefault(p => p != patient && !p.Dead && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                if (surgeon == null) return new { success = false, reason = "No second colonist who can doctor." };
                return new
                {
                    success = true, patientId = patient.GetUniqueLoadID(), part = patient.RaceProps.body.AllParts.IndexOf(leg),
                    surgeonId = surgeon.GetUniqueLoadID(),
                    prisonerId = prisoner.GetUniqueLoadID(), kidney = prisoner.RaceProps.body.AllParts.IndexOf(kidney),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/medical_plague_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Add Plague to two disposable colonists, one untended and one tended, for native disease readback.")]
        public async Task<object> Plague(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Stage the disease survival case: both untended, five herbal and five industrial medicine, medical sleeping spots.", DefaultValue = false)] bool survival = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Find.CurrentMap == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("Paused disposable colony required");
                var people = Find.CurrentMap.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Take(2).ToArray();
                if (people.Length != 2) throw new InvalidOperationException("Two colonists required");
                var def = HediffDef.Named("Plague");
                foreach (var pawn in people) {
                    foreach (var old in pawn.health.hediffSet.hediffs.Where(h => h.def == def).ToArray())
                        pawn.health.RemoveHediff(old);
                    var plague = HediffMaker.MakeHediff(def, pawn);
                    plague.Severity = .2f;
                    pawn.health.AddHediff(plague);
                    // Pin the rolled progression factor so the readback rate is exact.
                    typeof(HediffComp_Immunizable).GetField("severityPerDayNotImmuneRandomFactor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .SetValue(plague.TryGetComp<HediffComp_Immunizable>(), 1f);
                    // Seed the precondition normally created on the first immunity tick.
                    pawn.health.immunity.TryAddImmunityRecord(def, def);
                    pawn.health.immunity.GetImmunityRecord(def).immunity = .1f;
                    if (!survival && pawn == people[1]) {
                        plague.Tended(.75f, .75f);
                        // Tended jitters quality by +-0.25 under the max; pin it so the rate is exact.
                        plague.TryGetComp<HediffComp_TendDuration>().tendQuality = .75f;
                    }
                }
                if (survival) {
                    var map = Find.CurrentMap;
                    foreach (var item in map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine).ToArray()) item.Destroy();
                    foreach (var pawn in map.mapPawns.FreeColonistsSpawned) {
                        foreach (var item in pawn.inventory.innerContainer.Where(t => t.def.IsMedicine).ToArray()) item.Destroy();
                        pawn.drafter.Drafted = false;
                        pawn.playerSettings.medCare = MedicalCareCategory.HerbalOrWorse;
                        pawn.workSettings.EnableAndInitialize();
                        foreach (var work in new[] { "Doctor", "Patient", "PatientBedRest" }) {
                            var workDef = DefDatabase<WorkTypeDef>.GetNamed(work);
                            if (!pawn.WorkTypeIsDisabled(workDef)) pawn.workSettings.SetPriority(workDef, work == "PatientBedRest" ? 0 : 1);
                        }
                    }
                    foreach (var name in new[] { "MedicineHerbal", "MedicineIndustrial" }) {
                        var item = ThingMaker.MakeThing(ThingDef.Named(name)); item.stackCount = 5;
                        if (!GenPlace.TryPlaceThing(item, people[0].Position, map, ThingPlaceMode.Near))
                            throw new InvalidOperationException("Medicine placement failed");
                        item.SetForbidden(false, false);
                    }
                    var cells = GenRadial.RadialCellsAround(people[0].Position, 12, true)
                        .Where(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map)
                            && c.GetEdifice(map) == null && map.thingGrid.ThingsListAt(c).Count == 0).Take(2).ToArray();
                    if (cells.Length != 2) throw new InvalidOperationException("Two medical sleeping cells required");
                    foreach (var cell in cells) {
                        var bed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("SleepingSpot"));
                        bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, cell, map); bed.Medical = true;
                    }
                }
                return new { success = true, patients = people.Select(p => p.GetUniqueLoadID()).ToArray() };
            }, cancellationToken).ConfigureAwait(false);
        }
        [Tool("test/medical_management_setup", Description = "Disposable B23 initial disease, injury, beds and supplies. Never performs treatment or surgery.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Include two initial flu patients.", DefaultValue = true)] bool disease = true,
            [ToolParameter(Description = "Set the disposable native recipe success factor to zero to exercise real surgical failure.", DefaultValue = false)] bool failSurgery = false,
            [ToolParameter(Description = "Disable routine Doctor work to exercise repeated explicit native tending.", DefaultValue = false)] bool manualTending = false,
            [ToolParameter(Description = "Force the surgical patient into the high-severity withdrawal stage of GoJuiceAddiction, instead of waiting on real decay/timing.", DefaultValue = false)] bool withdrawal = false,
            [ToolParameter(Description = "Hospital planning variant: flu patients start tended, no medical sleeping spots are placed and PatientBedRest stays enabled, so the patients need a hosted medical bed the service must provide.", DefaultValue = false)] bool hospital = false,
            [ToolParameter(Description = "surgery/elective-share (#1848, epic #1829): rich stocks 6000 gold, poor destroys every loose item but wood, medicine, the prosthetic leg, the two bionic parts and 8 meals, so the colonists' personal shares differ. Used with condition elective.", DefaultValue = "")] string wealth = "",
            [ToolParameter(Description = "Surgery cases (#1170): give the second colonist cataract, infectedHand, missingKidney or missingArm; missingKidney and missingArm also hold one unrecruitable non-player prisoner on a prisoner sleeping spot, under missingArm with a BionicArm. missingKidneyIndustrial is missingKidney without the herbal stock, so industrial medicine is the only medicine (#1239). pegTraining (#1236) instead drops every doctor to Medicine 8 and holds one unrecruitable factionless prisoner missing a leg. elective (#1848) adds no condition: the surgery room and hospital bed stand, a BionicEye and a BionicArm are stocked for healthy colonists, and the third colonist's missing leg is the one served operation. Empty adds nothing.", DefaultValue = "")] string condition = "")
        {
            var industrialOnly = condition == "missingKidneyIndustrial";
            if (industrialOnly) condition = "missingKidney";
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var stage = "colony";
                try {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                if (people.Count < 3) throw new InvalidOperationException("Three colonists required");
                Current.Game.storyteller = new Storyteller(Find.Storyteller.def, DefDatabase<DifficultyDef>.GetNamed("Peaceful"));
                var center = people[0].Position;
                stage = "staff";
                foreach (var pawn in people) {
                    pawn.drafter.Drafted = false;
                    pawn.playerSettings.medCare = MedicalCareCategory.Best;
                    pawn.workSettings.EnableAndInitialize();
                    foreach (var skill in pawn.skills.skills.Where(s => s.def == SkillDefOf.Medicine)) {
                        skill.Level = condition == "pegTraining" ? 8 : 20;
                        // The pawn read carries levels, not XP: start 1000 XP short
                        // of level 9 so one peg-leg cycle shows as a level.
                        if (condition == "pegTraining") { skill.passion = Passion.Major; skill.xpSinceLastLevel = skill.XpRequiredForLevelUp - 1000f; }
                    }
                    foreach (var work in new[] { "Doctor", "Patient", "PatientBedRest" }) {
                        var def = DefDatabase<WorkTypeDef>.GetNamed(work);
                        if (!pawn.WorkTypeIsDisabled(def)) pawn.workSettings.SetPriority(def, (disease && !hospital && work == "PatientBedRest") || (manualTending && work == "Doctor") ? 0 : 1);
                    }
                }
                // Surgery cases need a room within the 20% restore failure cap:
                // outdoors on a sleeping spot vanilla scores ~38% even for
                // Medicine 20 doctors.
                var prisonCell = IntVec3.Invalid;
                if (condition != "") {
                    stage = "surgery room";
                    SurgeryRoom(map, center + new IntVec3(4, 0, -2), false);
                    if (condition == "missingKidney" || condition == "missingArm" || condition == "pegTraining") prisonCell = SurgeryRoom(map, center + new IntVec3(-9, 0, -2), true);
                }
                foreach (var item in new[] { ("MealSurvivalPack", 200), ("MedicineIndustrial", 60), ("WoodLog", 100), ("SimpleProstheticLeg", 1) }) {
                    stage = "stock "+item.Item1;
                    var def = ThingDef.Named(item.Item1);
                    for (var left = item.Item2; left > 0;) {
                        var thing = ThingMaker.MakeThing(def); thing.stackCount = Math.Min(left, def.stackLimit); left -= thing.stackCount;
                        if (!GenPlace.TryPlaceThing(thing, center, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Stock fixture placement failed");
                        thing.SetForbidden(false, false);
                    }
                }
                foreach (var cell in GenRadial.RadialCellsAround(center, 12, true).Where(c => c.InBounds(map) && c.Standable(map)
                    && !c.Fogged(map) && c.GetEdifice(map) == null && map.thingGrid.ThingsListAt(c).Count == 0).Take(hospital || condition != "" ? 0 : 4).ToArray()) {
                    stage = "bed";
                    var bed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("SleepingSpot"));
                    bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, cell, map); bed.Medical = true;
                }
                foreach (var pawn in people.Take(disease ? 2 : 0)) {
                    stage = "disease";
                    var flu = HediffMaker.MakeHediff(HediffDef.Named("Flu"), pawn); flu.Severity = .05f;
                    pawn.health.AddHediff(flu);
                    // Hospital planning starts from a tended patient: an untended
                    // hediff is a critical-medical emergency that suspends every
                    // routine goal, and the bed is what the service must provide.
                    if (hospital) {
                        flu.Tended(1f, 1f);
                        // Hold the tend for the whole run: a lapsed tend would raise the
                        // emergency again and suspend the goal mid-conversion.
                        var tend = flu.TryGetComp<HediffComp_TendDuration>();
                        if (tend != null) tend.tendTicksLeft = 1000000;
                    }
                }
                var surgical = people[2];
                surgicalId = surgical.thingIDNumber;
                if (failSurgery) {
                    var recipe = DefDatabase<RecipeDef>.GetNamed("InstallPegLeg");
                    recipe.surgerySuccessChanceFactor = 0;
                    recipe.surgeryOutcomeEffect = new SurgeryOutcomeEffectDef {
                        outcomes = new System.Collections.Generic.List<SurgeryOutcome> {
                            DefDatabase<SurgeryOutcomeEffectDef>.GetNamed("SurgeryOutcomeBase").outcomes.Last() } };
                }
                stage = "missing leg";
                var leg = surgical.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == "Leg");
                surgical.health.AddHediff(HediffDefOf.MissingBodyPart, leg);
                string withdrawalPatient = null;
                if (withdrawal) {
                    stage = "withdrawal";
                    // Reuses the surgical patient rather than requiring a fourth colonist: the
                    // standard debug-game-ready scenario only spawns three, and nothing about
                    // Hediff_Addiction conflicts with the unrelated missing-leg hediff already on
                    // this pawn.
                    var addict = surgical;
                    // Hediff_Addiction.CurStageIndex switches to the "withdrawal" stage once
                    // Severity reaches its floor, not via a stage-declared minSeverity (GoJuiceAddiction's
                    // withdrawal stage has none) -- setting it to zero here forces the stage
                    // immediately, mirroring the disposable flu.Severity assignment above rather
                    // than waiting on the real SeverityPerDay decay this addiction otherwise needs
                    // roughly eleven days of real abstinence to reach.
                    var addiction = HediffMaker.MakeHediff(HediffDef.Named("GoJuiceAddiction"), addict);
                    addiction.Severity = 0f;
                    addict.health.AddHediff(addiction);
                    withdrawalPatient = addict.GetUniqueLoadID();
                }
                string conditionPatient = null, prisonerId = null;
                var conditionPart = -1;
                if (condition != "") {
                    stage = "condition " + condition;
                    var patient = people[1];
                    conditionPatient = patient.GetUniqueLoadID();
                    string partDef, stock = null;
                    switch (condition) {
                    case "restore": partDef = null; break;
                    case "cataract": partDef = "Eye"; stock = "BionicEye"; break;
                    case "infectedHand": partDef = "Hand"; break;
                    case "missingKidney": partDef = "Kidney"; break;
                    case "missingArm": partDef = "Shoulder"; break;
                    case "pegTraining": partDef = null; break;
                    case "elective": partDef = null; break;
                    default: throw new InvalidOperationException("Unknown condition " + condition);
                    }
                    var part = partDef == null ? null : patient.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == partDef);
                    conditionPart = part == null ? -1 : patient.RaceProps.body.AllParts.IndexOf(part);
                    if (condition == "cataract") patient.health.AddHediff(HediffDef.Named("Cataract"), part);
                    if (condition == "missingKidney" || condition == "missingArm") patient.health.AddHediff(HediffDefOf.MissingBodyPart, part);
                    if (condition == "infectedHand") {
                        // Losing its immunity race even once tended: tended severity
                        // rises 0.84-0.53 = 0.31/day, so 0.7 reaches 1 in ~1 day while
                        // immunity from 0 needs ~1.55 days at 0.644/day.
                        var def = HediffDef.Named("WoundInfection");
                        var infection = HediffMaker.MakeHediff(def, patient, part); infection.Severity = .7f;
                        patient.health.AddHediff(infection, part);
                        typeof(HediffComp_Immunizable).GetField("severityPerDayNotImmuneRandomFactor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            .SetValue(infection.TryGetComp<HediffComp_Immunizable>(), 1f);
                        patient.health.immunity.TryAddImmunityRecord(def, def);
                        patient.health.immunity.GetImmunityRecord(def).immunity = 0f;
                    }
                    if (stock != null) {
                        var item = ThingMaker.MakeThing(ThingDef.Named(stock));
                        if (!GenPlace.TryPlaceThing(item, center, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Part placement failed");
                        item.SetForbidden(false, false);
                    }
                    if (condition == "missingKidney" || condition == "missingArm" || condition == "pegTraining") {
                        stage = "prisoner";
                        // Peg-leg training runs on a factionless prisoner: its removal
                        // costs no goodwill.
                        var faction = condition == "pegTraining" ? null : Find.FactionManager.AllFactionsListForReading.First(f => !f.IsPlayer && !f.Hidden && f.def.humanlikeFaction && !f.HostileTo(Faction.OfPlayer));
                        var prisoner = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, faction, forceGenerateNewPawn: true,
                            canGeneratePawnRelations: false, developmentalStages: DevelopmentalStage.Adult));
                        GenSpawn.Spawn(prisoner, prisonCell, map);
                        prisoner.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                        prisoner.guest.Recruitable = false;
                        if (condition == "missingArm") {
                            var shoulder = prisoner.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == "Shoulder");
                            prisoner.health.AddHediff(HediffDef.Named("BionicArm"), shoulder);
                        }
                        if (condition == "pegTraining") {
                            var prisonerLeg = prisoner.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == "Leg");
                            prisoner.health.AddHediff(HediffDefOf.MissingBodyPart, prisonerLeg);
                            conditionPart = prisoner.RaceProps.body.AllParts.IndexOf(prisonerLeg);
                        }
                        // A prisoner's default care allows herbal at best: stock it,
                        // or the harvest reads ingredients_on_map false.
                        if (!industrialOnly) {
                            var herbal = ThingMaker.MakeThing(ThingDef.Named("MedicineHerbal")); herbal.stackCount = 10;
                            if (!GenPlace.TryPlaceThing(herbal, center, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Herbal placement failed");
                            herbal.SetForbidden(false, false);
                        }
                        prisonerId = prisoner.GetUniqueLoadID();
                    }
                }
                if (condition == "elective") {
                    stage = "elective " + wealth;
                    if (wealth != "rich" && wealth != "poor") throw new InvalidOperationException("elective needs wealth rich or poor");
                    foreach (var name in new[] { "BionicEye", "BionicArm" }) {
                        var part = ThingMaker.MakeThing(ThingDef.Named(name));
                        if (!GenPlace.TryPlaceThing(part, center, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Elective part placement failed");
                        part.SetForbidden(false, false);
                    }
                    if (wealth == "poor") {
                        var keepDefs = new[] { "WoodLog", "MedicineIndustrial", "SimpleProstheticLeg", "BionicEye", "BionicArm" };
                        foreach (var item in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => t.Spawned && !keepDefs.Contains(t.def.defName)).ToList()) item.Destroy();
                        var meals = ThingMaker.MakeThing(ThingDef.Named("MealSurvivalPack")); meals.stackCount = 8;
                        if (!GenPlace.TryPlaceThing(meals, center, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Meal placement failed");
                        meals.SetForbidden(false, false);
                    } else {
                        var gold = ThingDef.Named("Gold");
                        for (var left = 6000; left > 0;) {
                            var stack = ThingMaker.MakeThing(gold); stack.stackCount = Math.Min(gold.stackLimit, left); left -= stack.stackCount;
                            if (!GenPlace.TryPlaceThing(stack, center, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Gold placement failed");
                            stack.SetForbidden(false, false);
                        }
                    }
                    map.wealthWatcher.ForceRecount();
                }
                return new { success = true, setupOnly = true, completedWorkInjected = false, failSurgery, wealthItems = map.wealthWatcher.WealthItems,
                    patients = people.Take(2).Select(p => p.GetUniqueLoadID()).ToArray(), surgical = surgical.GetUniqueLoadID(),
                    part = surgical.RaceProps.body.AllParts.IndexOf(leg), withdrawalPatient, conditionPatient, conditionPart, prisonerId,
                    tick = Find.TickManager.TicksGame };
                } catch (Exception error) { return new { success = false, stage, error = error.ToString() }; }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
