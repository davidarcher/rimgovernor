#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // Policy databases and per-pawn policy inputs. Read-only: the
    // bot's per-pawn policy planners compose their writes from these.
    internal static class NativePolicyFacts
    {
        // Player pawns whose policies the bot owns: its colonists, slaves and
        // tame animals, and its prisoners, wherever they are.
        private static List<Pawn> Owned()
        {
            var player = Faction.OfPlayerSilentFail;
            return PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive
                .Where(p => player != null && (p.Faction == player || p.HostFaction == player))
                .OrderBy(p => p.thingIDNumber).ToList();
        }

        private static RimGovernor.Brain.Domain.EquatableArray<Obs.PolicyEntry> Rows<T>(List<T> policies, List<Pawn> pawns, Func<Pawn, Policy?> current, Func<int, Obs.PolicyEntry, Obs.PolicyEntry>? enrich = null) where T : Policy
        {
            var rows = new List<Obs.PolicyEntry>();
            for (var i = 0; i < policies.Count; i++) {
                var policy = policies[i];
                var row = new Obs.PolicyEntry { Id = Id(policy.GetUniqueLoadID()), Label = Text(policy.label ?? ""), Default = i == 0 };
                row = row with { PawnIds = [.. row.PawnIds, .. pawns.Where(p => current(p) == policy).Select(p => Id(p.GetUniqueLoadID()))] };
                if (enrich != null) row = enrich(i, row);
                rows.Add(row);
            }
            return RimGovernor.Brain.Domain.EquatableArray.From(rows);
        }

        internal static Obs.PolicySection Read(Map map)
        {
            try {
                var game = Current.Game; var pawns = Owned(); var facts = new Obs.PolicyFacts();
                facts = facts with {
                    Outfit = Rows(game.outfitDatabase.AllOutfits, pawns, p => p.outfits?.CurrentApparelPolicy),
                    Drug = Rows(game.drugPolicyDatabase.AllPolicies, pawns, p => p.drugs?.CurrentPolicy,
                        (i, row) => row with { DrugEntries = [.. row.DrugEntries, .. NativeDrugPolicy.Entries(game.drugPolicyDatabase.AllPolicies[i]).Select(e => e with { DrugDef = Id(e.DrugDef) })] }),
                    Food = Rows(game.foodRestrictionDatabase.AllFoodRestrictions, pawns, p => p.foodRestriction?.CurrentFoodPolicy,
                        (i, row) => row with { AllowedDefs = [.. row.AllowedDefs, .. NativeFoodPolicy.Allowed(game.foodRestrictionDatabase.AllFoodRestrictions[i]).Select(d => Id(d))] }),
                    Reading = Rows(game.readingPolicyDatabase.AllReadingPolicies, pawns, p => p.reading?.CurrentPolicy,
                        (i, row) => row with { AllowedDefs = [.. row.AllowedDefs, .. NativeReadingPolicy.Allowed(game.readingPolicyDatabase.AllReadingPolicies[i]).Select(d => Id(d))] }),
                };
                foreach (var p in pawns.Where(p => p.MapHeld == map && !p.Dead && p.foodRestriction != null && p.needs?.food != null
                    && (p.IsPrisonerOfColony || p.RaceProps.Animal))) {
                    var row = new Obs.FoodEater { PawnId = Id(p.GetUniqueLoadID()) };
                    if (p.RaceProps.Animal) {
                        row = row with { Kind = Obs.FoodEaterKind.Animal };
                    } else {
                        row = row with { Kind = Obs.FoodEaterKind.Prisoner };
                        row = row with { PawnTraits = [.. row.PawnTraits, .. (p.story?.traits?.allTraits ?? new List<Trait>()).OrderBy(t => t.def.defName, StringComparer.Ordinal).ThenBy(t => t.Degree).Select(t => new Obs.Trait { DefName = Id(t.def.defName), Degree = t.Degree })] };
                        if (ModsConfig.IdeologyActive && p.Ideo is Ideo ideo)
                            row = row with { Precepts = [.. row.Precepts, .. ideo.PreceptsListForReading.Select(x => Id(x.def.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)] };
                    }
                    facts = facts with { FoodEaters = [.. facts.FoodEaters, row] };
                }
                foreach (var area in map.areaManager.AllAreas.OfType<Area_Allowed>()) {
                    var row = new Obs.AllowedAreaEntry { Id = Id(area.GetUniqueLoadID()), Label = Text(area.Label ?? "") };
                    row = row with { PawnIds = [.. row.PawnIds, .. pawns.Where(p => p.MapHeld == map && p.playerSettings?.AreaRestrictionInPawnCurrentMap == area)
                        .Select(p => Id(p.GetUniqueLoadID()))] };
                    facts = facts with { AllowedAreas = [.. facts.AllowedAreas, row] };
                }
                return new Obs.PolicySection { Outcome = facts };
            } catch (Exception e) {
                return new Obs.PolicySection { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Policy databases: " + e.GetType().Name } };
            }
        }

        private static Obs.ApparelRequirementFact Requirement(ApparelRequirement r)
        {
            var row = new Obs.ApparelRequirementFact();
            row = row with { BodyPartGroups = [.. row.BodyPartGroups, .. (r.bodyPartGroupsMatchAny ?? new List<BodyPartGroupDef>()).Select(d => Id(d.defName))] };
            row = row with { RequiredDefs = [.. row.RequiredDefs, .. (r.requiredDefs ?? new List<ThingDef>()).Select(d => Id(d.defName))] };
            row = row with { RequiredTags = [.. row.RequiredTags, .. (r.requiredTags ?? new List<string>()).Where(ProtoBoundary.IsIdentifier)] };
            row = row with { AllowedTags = [.. row.AllowedTags, .. (r.allowedTags ?? new List<string>()).Where(ProtoBoundary.IsIdentifier)] };
            return row;
        }

        internal static Obs.PawnPolicyInputs Inputs(Pawn pawn)
        {
            var row = new Obs.PawnPolicyInputs();
            if (pawn.outfits?.CurrentApparelPolicy is ApparelPolicy outfit) row = row with { OutfitPolicyId = Id(outfit.GetUniqueLoadID()) };
            if (pawn.drugs?.CurrentPolicy is DrugPolicy drug) row = row with { DrugPolicyId = Id(drug.GetUniqueLoadID()) };
            if (pawn.reading?.CurrentPolicy is ReadingPolicy reading) row = row with { ReadingPolicyId = Id(reading.GetUniqueLoadID()) };
            if (pawn.inventoryStock?.stockEntries is Dictionary<InventoryStockGroupDef, InventoryStockEntry> stock)
                foreach (var entry in stock.OrderBy(e => e.Key.defName, StringComparer.Ordinal))
                    row = row with { InventoryStock = [.. row.InventoryStock, new Obs.InventoryStockSetting { Group = Id(entry.Key.defName), ThingDef = Id(entry.Value.thingDef?.defName ?? entry.Key.DefaultThingDef.defName), Count = entry.Value.count }] };
            var hediffs = pawn.health?.hediffSet?.hediffs ?? new List<Hediff>();
            foreach (var chemical in DefDatabase<ChemicalDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal)) {
                var addiction = hediffs.OfType<Hediff_Addiction>().FirstOrDefault(h => h.Chemical == chemical);
                var tolerance = chemical.toleranceHediff == null ? null : hediffs.FirstOrDefault(h => h.def == chemical.toleranceHediff);
                if (addiction == null && tolerance == null) continue;
                var state = new Obs.ChemicalState { Chemical = Id(chemical.defName) };
                if (addiction != null) { state = state with { Addiction = Number(addiction.Severity) }; state = state with { Withdrawal = addiction.CurStageIndex == 1 }; }
                if (tolerance != null) state = state with { Tolerance = Number(tolerance.Severity) };
                row = row with { Chemicals = [.. row.Chemicals, state] };
            }
            if (pawn.genes != null)
                row = row with { DependencyChemicals = [.. row.DependencyChemicals, .. pawn.genes.GenesListForReading.OfType<Gene_ChemicalDependency>()
                    .Where(g => g.def.chemical != null).Select(g => Id(g.def.chemical.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)] };
            if (ModsConfig.RoyaltyActive && pawn.royalty?.MostSeniorTitle is RoyalTitle title)
                row = row with { RoyalTitle = Id(title.def.defName) };
            if (ModsConfig.IdeologyActive && pawn.Ideo is Ideo ideo) {
                row = row with { IdeoId = Id(ideo.GetUniqueLoadID()) };
                if (pawn.ideo != null) row = row with { IdeoCertainty = Number(pawn.ideo.Certainty) };
                row = row with { Precepts = [.. row.Precepts, .. ideo.PreceptsListForReading.Select(p => Id(p.def.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)] };
                row = row with { PreceptApparel = [.. row.PreceptApparel, .. ideo.PreceptsListForReading.OfType<Precept_Apparel>().Where(p => p.apparelDef != null)
                    .Select(p => Id(p.apparelDef.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)] };
                if (ideo.GetRole(pawn) is Precept_Role role) {
                    row = row with { IdeoRole = Id(role.def.defName) };
                    foreach (var r in role.ApparelRequirements ?? new List<PreceptApparelRequirement>())
                        if (r.requirement != null) row = row with { RoleApparel = [.. row.RoleApparel, Requirement(r.requirement)] };
                }
            }
            if (pawn.guest is Pawn_GuestTracker guest && pawn.HostFaction != null) {
                row = row with { GuestStatus = guest.GuestStatus.ToString() };
                if (guest.IsPrisoner && guest.ExclusiveInteractionMode != null) row = row with { PrisonerInteraction = Id(guest.ExclusiveInteractionMode.defName) };
                if (guest.IsSlave && guest.slaveInteractionMode != null) row = row with { SlaveInteraction = Id(guest.slaveInteractionMode.defName) };
            }
            if (!pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
                row = row with { MedicalTendQuality = Number(pawn.GetStatValue(StatDefOf.MedicalTendQuality)) };
            return row;
        }
    }
}
