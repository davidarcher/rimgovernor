#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class MedicalOperationsTool
    {
        [Tool("home/medical_operations", Description = "Discover current patient surgery recipes, body parts, ingredient and practitioner requirements. Queue one explicitly player-directed ordinary surgery bill. No health changes are applied by this tool; bill removal is not success. Recipes with extra confirmations or no supported health postcondition are inspection-only.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact current-map player colonist Thing ID.")] string patient,
            [ToolParameter(Description = "Exact discovered recipe; omit for inspection.", DefaultValue = "")] string recipe = "",
            [ToolParameter(Description = "Observed body part index; -1 for a whole-body recipe.", DefaultValue = -1)] int part = -1,
            [ToolParameter(Description = "Health identity signature from this tool; required to queue.", DefaultValue = "")] string expectedHealth = "",
            [ToolParameter(Description = "Observed patient medical care policy; required to queue.", DefaultValue = "")] string expectedCare = "",
            [ToolParameter(Description = "Exact observed colony identity; required to queue.", DefaultValue = "")] string colonyId = "",
            [ToolParameter(Description = "Exact observed load identity; required to queue.", DefaultValue = "")] string loadToken = "",
            [ToolParameter(Description = "Exact observed map identity; required to queue.", DefaultValue = -1)] int mapId = -1,
            [ToolParameter(Description = "Preview only unless explicitly false.", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (identity == null || map == null || ((!dryRun || !string.IsNullOrEmpty(colonyId))
                    && (identity.ColonyId != colonyId || identity.LoadToken != loadToken || map.uniqueID != mapId)))
                    return new { success = false, error = "Colony, map or load changed; inspect before operating" };
                var pawn = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == patient);
                if (pawn == null || pawn.Dead) return new { success = false, error = "Living current-map player patient required" };
                var health = string.Join(";", pawn.health.hediffSet.hediffs.OrderBy(h => h.loadID)
                    .Select(h => h.GetUniqueLoadID()+":"+h.def.defName+":"+(h.Part == null ? -1 : pawn.RaceProps.body.AllParts.IndexOf(h.Part))));
                var care = pawn.playerSettings?.medCare.ToString();
                var rows = new List<Dictionary<string, object?>>();
                RecipeDef? selected = null;
                BodyPartRecord? selectedPart = null;
                Dictionary<string, object?>? selection = null;
                foreach (var def in pawn.def.AllRecipes.Where(r => r.AvailableNow)) {
                    var report = def.Worker.AvailableReport(pawn);
                    if (!report.Accepted) continue;
                    var parts = def.targetsBodyPart ? def.Worker.GetPartsToApplyOn(pawn, def).ToList()
                        : new List<BodyPartRecord?> { null };
                    foreach (var bodyPart in parts) {
                        if (!def.AvailableOnNow(pawn, bodyPart)) continue;
                        var index = bodyPart == null ? -1 : pawn.RaceProps.body.AllParts.IndexOf(bodyPart);
                        var doctors = map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn && !p.Dead && !p.Downed
                            && !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                            && def.PawnSatisfiesSkillRequirements(p)).Select(p => p.GetUniqueLoadID()).ToArray();
                        var missing = def.PotentiallyMissingIngredients(null, map).Select(d => d.defName).ToArray();
                        var requiresMedicine = def.ingredients.Any(i => i.filter.AllowedThingDefs.Any(d => d.IsMedicine));
                        var medicine = map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine)
                            .Where(t => !t.IsForbidden(pawn) && !t.Position.Fogged(map) && pawn.playerSettings != null
                                && pawn.playerSettings.medCare.AllowsMedicine(t.def)
                                && def.ingredients.Any(i => i.filter.Allows(t)))
                            .GroupBy(t => t.def.defName).Select(g => new { definition = g.Key, count = g.Sum(t => t.stackCount) }).ToArray();
                        var confirmation = def.Worker.GetConfirmation(pawn).ToString();
                        var supports = (def.addsHediff != null || def.removesHediff != null)
                            && def.changesHediffLevel == null && string.IsNullOrEmpty(confirmation)
                            && !def.Worker.IsViolationOnPawn(pawn, bodyPart, Faction.OfPlayer);
                        if (def.addsHediff != null && (!CompRoyalImplant.CheckForViolations(pawn, def.addsHediff, def.hediffLevelOffset).NullOrEmpty()
                            || pawn.health.hediffSet.hediffs.Any(h => h.def == def.addsHediff && h.Part == bodyPart))) supports = false;
                        if (def.removesHediff != null && !pawn.health.hediffSet.hediffs.Any(h => h.def == def.removesHediff && h.Part == bodyPart && h.Visible)) supports = false;
                        var row = new Dictionary<string, object?> {
                            ["recipe"] = def.defName, ["label"] = def.Worker.GetLabelWhenUsedOn(pawn, bodyPart),
                            ["part"] = index, ["partLabel"] = bodyPart?.Label, ["supported"] = supports,
                            ["addsHediff"] = def.addsHediff?.defName, ["removesHediff"] = def.removesHediff?.defName,
                            ["practitioners"] = doctors, ["missingIngredients"] = missing, ["confirmation"] = confirmation,
                            ["requiresMedicine"] = requiresMedicine, ["medicinePermittedByCare"] = medicine,
                            ["hasPermittedMedicine"] = !requiresMedicine || medicine.Length > 0,
                            ["ingredients"] = def.ingredients.Select(i => new { count = i.GetBaseCount(),
                                definitions = i.filter.AllowedThingDefs.Select(d => d.defName).ToArray() }).ToArray(),
                            ["skills"] = def.skillRequirements?.Select(s => new { skill = s.skill.defName, level = s.minLevel }).ToArray(),
                            ["eligibilityLimit"] = "Native work selection still checks beds, medicine policy, quantities, access and reservations" };
                        rows.Add(row);
                        if (def.defName == recipe && index == part) { selected = def; selectedPart = bodyPart; selection = row; }
                    }
                }
                string? error = null;
                Bill_Medical? bill = null;
                if (!string.IsNullOrEmpty(recipe)) {
                    if (selection == null) error = "Recipe/body part is no longer eligible";
                    else if (!BridgeCommon.Flag(selection, "supported")) error = "Recipe needs an unsupported confirmation or health postcondition";
                    else if (!(selection["practitioners"] is string[] practitioners) || practitioners.Length == 0) error = "No available practitioner satisfies native skills";
                    else if (!(selection["missingIngredients"] is string[] missing) || missing.Length != 0) error = "Required native ingredients are unavailable";
                    else if (!BridgeCommon.Flag(selection, "hasPermittedMedicine")) error = "No observed medicine is permitted by the patient care policy and recipe";
                    else if (pawn.playerSettings == null || pawn.playerSettings.medCare <= MedicalCareCategory.NoMeds)
                        error = "Patient medical care policy does not permit surgery medicine";
                    else if (pawn.BillStack.Bills.Any()) error = "Existing patient bills require player review before adding an operation";
                    else if ((!string.IsNullOrEmpty(expectedHealth) && expectedHealth != health)
                        || (!string.IsNullOrEmpty(expectedCare) && expectedCare != care)) error = "Patient health or care policy changed";
                    if (!dryRun && error == null) {
                        if (!Find.TickManager.Paused || expectedHealth != health || expectedCare != care)
                            error = "Queueing requires paused current health and care policy evidence";
                        else bill = HealthCardUtility.CreateSurgeryBill(pawn, selected, selectedPart);
                    }
                } else if (!dryRun) error = "An exact discovered recipe is required";
                return new { success = error == null, error, patient, healthSignature = health, medicalCare = care,
                    colonyId = identity.ColonyId, loadToken = identity.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, recipes = rows, selected = selection, billId = bill?.GetUniqueLoadID(),
                    bills = pawn.BillStack.Bills.Select(b => new { id = b.GetUniqueLoadID(), recipe = b.recipe.defName,
                        part = b is Bill_Medical medical && medical.Part != null ? pawn.RaceProps.body.AllParts.IndexOf(medical.Part) : -1,
                        suspended = b.suspended }).ToArray(),
                    queued = bill != null, outcome = "Health outcome must be observed separately" };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
