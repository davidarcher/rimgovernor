using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds one deterministic patient
    // missing a leg (InstallPegLeg's own precondition, the same recipe
    // MedicalManagementFixture's failSurgery scenario already exercises) so
    // the surgeryaccept binary can queue and observe a real
    // HealthCardUtility.CreateSurgeryBill outcome without depending on native
    // random pawn/injury generation. Never applies the implant itself: that
    // is exactly the effect under test.
    public sealed class SurgeryFixture
    {
        [Tool("test/surgery_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: give one existing colonist a missing leg (InstallPegLeg's precondition), a second eligible colonist with Medicine 20 as practitioner, stocked Wood, and Best medical care for both. Never installs the implant.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var stage = "colony";
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null || Current.Game == null || !Find.TickManager.Paused)
                        return Refuse("A paused disposable colony map is required.");
                    var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.InMentalState)
                        .OrderBy(p => p.thingIDNumber).ToList();
                    if (people.Count < 2) return Refuse("At least two existing colonists are required.");
                    stage = "staff";
                    // The baseline eight-tribal save may already carry a colonist
                    // with a scar/backstory missing part; pick a patient that
                    // genuinely has none yet rather than assuming ordering.
                    var patient = people.FirstOrDefault(p => !p.health.hediffSet.hediffs.Any(h => h.def == HediffDefOf.MissingBodyPart)
                        && p.health.hediffSet.GetNotMissingParts().Any(part => part.def.defName == "Leg"));
                    if (patient == null) return Refuse("No existing colonist without a missing body part and with an intact leg was found.");
                    var doctor = people.First(p => p != patient);
                    foreach (var pawn in new[] { patient, doctor })
                    {
                        pawn.drafter.Drafted = false;
                        pawn.playerSettings.medCare = MedicalCareCategory.Best;
                    }
                    doctor.workSettings.EnableAndInitialize();
                    patient.workSettings.EnableAndInitialize();
                    foreach (var skill in doctor.skills.skills.Where(s => s.def == SkillDefOf.Medicine)) skill.Level = 20;
                    var doctorWork = DefDatabase<WorkTypeDef>.GetNamed("Doctor");
                    if (!doctor.WorkTypeIsDisabled(doctorWork)) doctor.workSettings.SetPriority(doctorWork, 1);
                    // The patient must actually go rest in the medical bed for
                    // the doctor's operation job to fire; enabling Patient and
                    // PatientBedRest (as MedicalManagementFixture does for its
                    // own surgery scenarios) lets JobGiver_PatientGoToBed send
                    // them there once the bill is queued.
                    foreach (var work in new[] { "Patient", "PatientBedRest" })
                    {
                        var def = DefDatabase<WorkTypeDef>.GetNamed(work);
                        if (!patient.WorkTypeIsDisabled(def)) patient.workSettings.SetPriority(def, 1);
                    }
                    if (patient.BillStack != null) foreach (var bill in patient.BillStack.Bills.ToArray()) patient.BillStack.Delete(bill);

                    stage = "stock ingredients";
                    var center = patient.Position;
                    // InstallPegLeg's ingredient list in this package set includes
                    // both Wood and Medicine (matching MedicalManagementFixture's
                    // own stock for the same recipe); stock both rather than
                    // assuming a wood-only bill of materials.
                    foreach (var item in new[] { ("WoodLog", 100), ("MedicineIndustrial", 60) })
                    {
                        var def = ThingDef.Named(item.Item1);
                        for (var left = item.Item2; left > 0;)
                        {
                            var thing = ThingMaker.MakeThing(def);
                            thing.stackCount = Math.Min(left, def.stackLimit);
                            left -= thing.stackCount;
                            if (!GenPlace.TryPlaceThing(thing, center, map, ThingPlaceMode.Near))
                                return Refuse("Stock fixture placement failed: " + item.Item1);
                            thing.SetForbidden(false, false);
                        }
                    }

                    stage = "bed";
                    // Native surgery work selection (WorkGiver_DoBill for medical
                    // recipes) requires the patient resting in a medical bed
                    // before a doctor will start the operation job; without one
                    // the bill stays queued forever. MedicalManagementFixture's
                    // own tend/surgery scenarios always place one for the same
                    // reason - mirror that here.
                    var bedCell = GenRadial.RadialCellsAround(center, 12, true)
                        .FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map)
                            && c.GetEdifice(map) == null && map.thingGrid.ThingsListAt(c).Count == 0);
                    if (bedCell == default) return Refuse("No open cell found near the patient to place a medical bed.");
                    var bed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("SleepingSpot"));
                    bed.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(bed, bedCell, map);
                    bed.Medical = true;

                    stage = "missing leg";
                    if (patient.health.hediffSet.hediffs.Any(h => h.def == HediffDefOf.MissingBodyPart))
                        return Refuse("Patient already has a missing body part; use a fresh disposable colony.");
                    var leg = patient.health.hediffSet.GetNotMissingParts().First(p => p.def.defName == "Leg");
                    patient.health.AddHediff(HediffDefOf.MissingBodyPart, leg);
                    var partIndex = patient.RaceProps.body.AllParts.IndexOf(leg);

                    // Force a guaranteed outcome: acceptance exercises the real
                    // native queue-and-complete path, not surgery's inherent
                    // success-chance roll (MedicalManagementFixture's own
                    // failSurgery scenario covers the failure roll separately).
                    stage = "force success";
                    var recipeDef = DefDatabase<RecipeDef>.GetNamed("InstallPegLeg");
                    recipeDef.surgerySuccessChanceFactor = 999f;

                    var identity = Current.Game.GetComponent<ColonyIdentity>();
                    return new
                    {
                        success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                        tick = Find.TickManager.TicksGame,
                        patientId = patient.GetUniqueLoadID(), doctorId = doctor.GetUniqueLoadID(),
                        recipe = "InstallPegLeg", part = partIndex,
                    };
                }
                catch (Exception error) { return new { success = false, stage, error = error.ToString() }; }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
