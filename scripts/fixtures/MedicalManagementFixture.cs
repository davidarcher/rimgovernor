using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    public sealed class MedicalManagementFixture
    {
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

        [Tool("test/medical_plague_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Add Plague to two disposable colonists, one untended and one tended, for native disease readback.")]
        public async Task<object> Plague(INativeContext ctx, CancellationToken cancellationToken)
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
                    (BridgeCommon.PrivateInstanceField(typeof(HediffComp_Immunizable), "severityPerDayNotImmuneRandomFactor") ?? throw new System.MissingFieldException(typeof(HediffComp_Immunizable).FullName, "severityPerDayNotImmuneRandomFactor"))
                        .SetValue(plague.TryGetComp<HediffComp_Immunizable>(), 1f);
                    // Seed the precondition normally created on the first immunity tick.
                    pawn.health.immunity.TryAddImmunityRecord(def, def);
                    pawn.health.immunity.GetImmunityRecord(def).immunity = .1f;
                    if (pawn == people[1]) {
                        plague.Tended(.75f, .75f);
                        // Tended jitters quality by +-0.25 under the max; pin it so the rate is exact.
                        plague.TryGetComp<HediffComp_TendDuration>().tendQuality = .75f;
                    }
                }
                return new { success = true, patients = people.Select(p => p.GetUniqueLoadID()).ToArray() };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/medical_management_setup", Description = "Disposable surgery setup: doctors, a colonist missing a leg, supplies and an optional condition. Never performs treatment or surgery.")]
        public async Task<object> Setup(INativeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Include two initial flu patients.", DefaultValue = true)] bool disease = true,
            [ToolParameter(Description = "Give the second colonist restore (nothing), cataract, infectedHand, or missingKidney, and raise a sterile surgery room; missingKidney also holds one unrecruitable non-player prisoner. missingKidneyIndustrial is missingKidney without the herbal stock (#1239). Empty adds nothing.", DefaultValue = "")] string condition = "")
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
                    foreach (var skill in pawn.skills.skills.Where(s => s.def == SkillDefOf.Medicine)) skill.Level = 20;
                    foreach (var work in new[] { "Doctor", "Patient", "PatientBedRest" }) {
                        var def = DefDatabase<WorkTypeDef>.GetNamed(work);
                        if (!pawn.WorkTypeIsDisabled(def)) pawn.workSettings.SetPriority(def, disease && work == "PatientBedRest" ? 0 : 1);
                    }
                }
                // Surgery needs a room within the 20% restore failure cap:
                // outdoors on a sleeping spot vanilla scores ~38% even for
                // Medicine 20 doctors.
                var prisonCell = IntVec3.Invalid;
                if (condition != "") {
                    stage = "surgery room";
                    SurgeryRoom(map, center + new IntVec3(4, 0, -2), false);
                    if (condition == "missingKidney") prisonCell = SurgeryRoom(map, center + new IntVec3(-9, 0, -2), true);
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
                    && !c.Fogged(map) && c.GetEdifice(map) == null && map.thingGrid.ThingsListAt(c).Count == 0).Take(condition != "" ? 0 : 4).ToArray()) {
                    stage = "bed";
                    var bed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("SleepingSpot"));
                    bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, cell, map); bed.Medical = true;
                }
                foreach (var pawn in people.Take(disease ? 2 : 0)) {
                    stage = "disease";
                    var flu = HediffMaker.MakeHediff(HediffDef.Named("Flu"), pawn); flu.Severity = .05f;
                    pawn.health.AddHediff(flu);
                }
                var surgical = people[2];
                stage = "missing leg";
                var leg = surgical.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == "Leg");
                surgical.health.AddHediff(HediffDefOf.MissingBodyPart, leg);
                string? conditionPatient = null, prisonerId = null;
                var conditionPart = -1;
                if (condition != "") {
                    stage = "condition " + condition;
                    var patient = people[1];
                    conditionPatient = patient.GetUniqueLoadID();
                    string? partDef, stock = null;
                    switch (condition) {
                    case "restore": partDef = null; break;
                    case "cataract": partDef = "Eye"; stock = "BionicEye"; break;
                    case "infectedHand": partDef = "Hand"; break;
                    case "missingKidney": partDef = "Kidney"; break;
                    default: throw new InvalidOperationException("Unknown condition " + condition);
                    }
                    var part = partDef == null ? null : patient.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == partDef);
                    conditionPart = part == null ? -1 : patient.RaceProps.body.AllParts.IndexOf(part);
                    if (condition == "cataract") patient.health.AddHediff(HediffDef.Named("Cataract"), part);
                    if (condition == "missingKidney") patient.health.AddHediff(HediffDefOf.MissingBodyPart, part);
                    if (condition == "infectedHand") {
                        // Losing its immunity race even once tended: tended severity
                        // rises 0.84-0.53 = 0.31/day, so 0.7 reaches 1 in ~1 day while
                        // immunity from 0 needs ~1.55 days at 0.644/day.
                        var def = HediffDef.Named("WoundInfection");
                        var infection = HediffMaker.MakeHediff(def, patient, part); infection.Severity = .7f;
                        patient.health.AddHediff(infection, part);
                        (BridgeCommon.PrivateInstanceField(typeof(HediffComp_Immunizable), "severityPerDayNotImmuneRandomFactor") ?? throw new System.MissingFieldException(typeof(HediffComp_Immunizable).FullName, "severityPerDayNotImmuneRandomFactor"))
                            .SetValue(infection.TryGetComp<HediffComp_Immunizable>(), 1f);
                        patient.health.immunity.TryAddImmunityRecord(def, def);
                        patient.health.immunity.GetImmunityRecord(def).immunity = 0f;
                    }
                    if (stock != null) {
                        var item = ThingMaker.MakeThing(ThingDef.Named(stock));
                        if (!GenPlace.TryPlaceThing(item, center, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Part placement failed");
                        item.SetForbidden(false, false);
                    }
                    if (condition == "missingKidney") {
                        stage = "prisoner";
                        var faction = Find.FactionManager.AllFactionsListForReading.First(f => !f.IsPlayer && !f.Hidden && f.def.humanlikeFaction && !f.HostileTo(Faction.OfPlayer));
                        var prisoner = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, faction, forceGenerateNewPawn: true,
                            canGeneratePawnRelations: false, developmentalStages: DevelopmentalStage.Adult));
                        GenSpawn.Spawn(prisoner, prisonCell, map);
                        prisoner.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                        prisoner.guest.Recruitable = false;
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
                return new { success = true, surgical = surgical.GetUniqueLoadID(), part = surgical.RaceProps.body.AllParts.IndexOf(leg),
                    conditionPatient, conditionPart, prisonerId };
                } catch (Exception error) { return new { success = false, error = stage + ": " + error }; }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
