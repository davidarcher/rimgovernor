#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // Policy databases and per-pawn policy inputs (#1297). Read-only: the
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

        private static void Add<T>(Google.Protobuf.Collections.RepeatedField<Obs.PolicyEntry> rows, List<T> policies, List<Pawn> pawns, Func<Pawn, Policy?> current) where T : Policy
        {
            for (var i = 0; i < policies.Count; i++) {
                var policy = policies[i];
                var row = new Obs.PolicyEntry { Id = Id(policy.GetUniqueLoadID()), Label = Text(policy.label ?? ""), Default = i == 0 };
                row.PawnIds.Add(pawns.Where(p => current(p) == policy).Select(p => Id(p.GetUniqueLoadID())));
                rows.Add(row);
            }
        }

        internal static Obs.PolicySection Read(Map map)
        {
            try {
                var game = Current.Game; var pawns = Owned(); var facts = new Obs.PolicyFacts();
                Add(facts.Outfit, game.outfitDatabase.AllOutfits, pawns, p => p.outfits?.CurrentApparelPolicy);
                Add(facts.Drug, game.drugPolicyDatabase.AllPolicies, pawns, p => p.drugs?.CurrentPolicy);
                Add(facts.Food, game.foodRestrictionDatabase.AllFoodRestrictions, pawns, p => p.foodRestriction?.CurrentFoodPolicy);
                Add(facts.Reading, game.readingPolicyDatabase.AllReadingPolicies, pawns, p => p.reading?.CurrentPolicy);
                foreach (var area in map.areaManager.AllAreas.OfType<Area_Allowed>()) {
                    var row = new Obs.AllowedAreaEntry { Id = Id(area.GetUniqueLoadID()), Label = Text(area.Label ?? "") };
                    row.PawnIds.Add(pawns.Where(p => p.MapHeld == map && p.playerSettings?.AreaRestrictionInPawnCurrentMap == area)
                        .Select(p => Id(p.GetUniqueLoadID())));
                    facts.AllowedAreas.Add(row);
                }
                return new Obs.PolicySection { Observed = facts };
            } catch (Exception e) {
                return new Obs.PolicySection { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Policy databases: " + e.GetType().Name } };
            }
        }

        private static Obs.ApparelRequirementFact Requirement(ApparelRequirement r)
        {
            var row = new Obs.ApparelRequirementFact();
            row.BodyPartGroups.Add((r.bodyPartGroupsMatchAny ?? new List<BodyPartGroupDef>()).Select(d => Id(d.defName)));
            row.RequiredDefs.Add((r.requiredDefs ?? new List<ThingDef>()).Select(d => Id(d.defName)));
            row.RequiredTags.Add((r.requiredTags ?? new List<string>()).Where(ProtoBoundary.IsIdentifier));
            row.AllowedTags.Add((r.allowedTags ?? new List<string>()).Where(ProtoBoundary.IsIdentifier));
            return row;
        }

        internal static Obs.PawnPolicyInputs Inputs(Pawn pawn)
        {
            var row = new Obs.PawnPolicyInputs();
            if (pawn.outfits?.CurrentApparelPolicy is ApparelPolicy outfit) row.OutfitPolicyId = Id(outfit.GetUniqueLoadID());
            if (pawn.drugs?.CurrentPolicy is DrugPolicy drug) row.DrugPolicyId = Id(drug.GetUniqueLoadID());
            if (pawn.reading?.CurrentPolicy is ReadingPolicy reading) row.ReadingPolicyId = Id(reading.GetUniqueLoadID());
            if (pawn.inventoryStock?.stockEntries is Dictionary<InventoryStockGroupDef, InventoryStockEntry> stock)
                foreach (var entry in stock.OrderBy(e => e.Key.defName, StringComparer.Ordinal))
                    row.InventoryStock.Add(new Obs.InventoryStockSetting { Group = Id(entry.Key.defName), ThingDef = Id(entry.Value.thingDef?.defName ?? entry.Key.DefaultThingDef.defName), Count = entry.Value.count });
            var hediffs = pawn.health?.hediffSet?.hediffs ?? new List<Hediff>();
            foreach (var chemical in DefDatabase<ChemicalDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal)) {
                var addiction = hediffs.OfType<Hediff_Addiction>().FirstOrDefault(h => h.Chemical == chemical);
                var tolerance = chemical.toleranceHediff == null ? null : hediffs.FirstOrDefault(h => h.def == chemical.toleranceHediff);
                if (addiction == null && tolerance == null) continue;
                var state = new Obs.ChemicalState { Chemical = Id(chemical.defName) };
                if (addiction != null) { state.Addiction = Number(addiction.Severity); state.Withdrawal = addiction.CurStageIndex == 1; }
                if (tolerance != null) state.Tolerance = Number(tolerance.Severity);
                row.Chemicals.Add(state);
            }
            if (pawn.genes != null)
                row.DependencyChemicals.Add(pawn.genes.GenesListForReading.OfType<Gene_ChemicalDependency>()
                    .Where(g => g.def.chemical != null).Select(g => Id(g.def.chemical.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal));
            if (ModsConfig.RoyaltyActive && pawn.royalty?.MostSeniorTitle is RoyalTitle title) {
                row.RoyalTitle = Id(title.def.defName);
                foreach (var r in title.def.requiredApparel ?? new List<ApparelRequirement>()) row.TitleApparel.Add(Requirement(r));
            }
            if (ModsConfig.IdeologyActive && pawn.Ideo is Ideo ideo) {
                row.IdeoId = Id(ideo.GetUniqueLoadID());
                row.Precepts.Add(ideo.PreceptsListForReading.Select(p => Id(p.def.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal));
                row.PreceptApparel.Add(ideo.PreceptsListForReading.OfType<Precept_Apparel>().Where(p => p.apparelDef != null)
                    .Select(p => Id(p.apparelDef.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal));
                if (ideo.GetRole(pawn) is Precept_Role role) {
                    row.IdeoRole = Id(role.def.defName);
                    foreach (var r in role.ApparelRequirements ?? new List<PreceptApparelRequirement>())
                        if (r.requirement != null) row.RoleApparel.Add(Requirement(r.requirement));
                }
            }
            if (pawn.guest is Pawn_GuestTracker guest && pawn.HostFaction != null) {
                row.GuestStatus = guest.GuestStatus.ToString();
                if (guest.IsPrisoner && guest.ExclusiveInteractionMode != null) row.PrisonerInteraction = Id(guest.ExclusiveInteractionMode.defName);
                if (guest.IsSlave && guest.slaveInteractionMode != null) row.SlaveInteraction = Id(guest.slaveInteractionMode.defName);
            }
            return row;
        }
    }
}
