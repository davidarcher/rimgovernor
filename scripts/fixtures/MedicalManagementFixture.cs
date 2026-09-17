using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class MedicalManagementFixture
    {
        private static int surgicalId;
        [Tool("test/medical_management_setup", Description = "Disposable B23 initial disease, injury, beds and supplies. Never performs treatment or surgery.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Include two initial flu patients.", DefaultValue = true)] bool disease = true,
            [ToolParameter(Description = "Set the disposable native recipe success factor to zero to exercise real surgical failure.", DefaultValue = false)] bool failSurgery = false,
            [ToolParameter(Description = "Disable routine Doctor work to exercise repeated explicit native tending.", DefaultValue = false)] bool manualTending = false,
            [ToolParameter(Description = "Force the surgical patient into the high-severity withdrawal stage of GoJuiceAddiction, instead of waiting on real decay/timing.", DefaultValue = false)] bool withdrawal = false,
            [ToolParameter(Description = "Hospital planning variant: flu patients start tended, no medical sleeping spots are placed and PatientBedRest stays enabled, so the patients need a hosted medical bed the service must provide.", DefaultValue = false)] bool hospital = false)
        {
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
                        if (!pawn.WorkTypeIsDisabled(def)) pawn.workSettings.SetPriority(def, (disease && !hospital && work == "PatientBedRest") || (manualTending && work == "Doctor") ? 0 : 1);
                    }
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
                    && !c.Fogged(map) && c.GetEdifice(map) == null && map.thingGrid.ThingsListAt(c).Count == 0).Take(hospital ? 0 : 4).ToArray()) {
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
                return new { success = true, setupOnly = true, completedWorkInjected = false, failSurgery,
                    patients = people.Take(2).Select(p => p.GetUniqueLoadID()).ToArray(), surgical = surgical.GetUniqueLoadID(),
                    part = surgical.RaceProps.body.AllParts.IndexOf(leg), withdrawalPatient, tick = Find.TickManager.TicksGame };
                } catch (Exception error) { return new { success = false, stage, error = error.ToString() }; }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/medical_management_change", Description = "Disposable shortage fixture: forbid or restore medicine, draft or release the available doctors. No treatment outcomes injected.")]
        public async Task<object> Change(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "supplies-off, supplies-on, doctors-off or doctors-on.")] string op)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused fixture required");
                var map = Find.CurrentMap;
                if (op == "supplies-off" || op == "supplies-on") {
                    foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine).ToArray())
                        thing.SetForbidden(op == "supplies-off", false);
                } else if (op == "doctors-off" || op == "doctors-on") {
                    foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p.thingIDNumber != surgicalId).ToArray())
                        pawn.drafter.Drafted = op == "doctors-off";
                } else throw new ArgumentException("Unknown fixture change");
                return new { success = true, op, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
