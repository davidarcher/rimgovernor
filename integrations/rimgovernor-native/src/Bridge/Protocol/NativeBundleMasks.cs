#nullable enable
using System.Linq;
using Google.Protobuf.Collections;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The bundle's field-mask contract for its continuous families (issues
    /// #360, #648), in one place. An absent mask is the whole family, exactly
    /// as the dedicated read returns it; a present mask keeps only the optional
    /// blocks whose include flag is true, so an empty mask is the slim family.
    /// An omitted block carries neither its values nor issue rows about them,
    /// since the controller asked for its absence.
    ///
    /// The readers (NativePawnObservationTools/NativePawnDetails,
    /// NativePopulationObservation, NativeResearchObservationTools) consult
    /// these predicates at source, so an omitted block is never read from
    /// native nor allocated. The Apply projections below are the reference
    /// the native contract probe compares a source-masked capture against: a
    /// full capture stripped here must equal it. The bundle no longer calls them.
    /// </summary>
    internal static class NativeBundleMasks
    {
        internal static bool GearDetail(Obs.PawnFields? mask) => mask == null || mask.IncludeGearDetail;
        internal static bool Inventory(Obs.PawnFields? mask) => mask == null || mask.IncludeInventory;
        internal static bool Capacities(Obs.PawnFields? mask) => mask == null || mask.IncludeCapacities;
        internal static bool SurgeryBills(Obs.PawnFields? mask) => mask == null || mask.IncludeSurgeryBills;
        internal static bool Backstory(Obs.PawnFields? mask) => mask == null || mask.IncludeBackstory;
        internal static bool Traits(Obs.PawnFields? mask) => mask == null || mask.IncludeTraits;
        internal static bool Relations(Obs.PawnFields? mask) => mask == null || mask.IncludeRelations;
        internal static bool OwnedBed(Obs.PopulationFields? mask) => mask == null || mask.IncludeOwnedBed;
        internal static bool Nutrition(Obs.PopulationFields? mask) => mask == null || mask.IncludeNutrition;
        internal static bool SupportedInteractions(Obs.PopulationFields? mask) => mask == null || mask.IncludeSupportedInteractions;
        internal static bool Unlocks(Obs.ResearchFields? mask) => mask == null || mask.IncludeUnlocks;
        internal static bool Costs(Obs.ResearchFields? mask) => mask == null || mask.IncludeCosts;
        internal static bool Facilities(Obs.ResearchFields? mask) => mask == null || mask.IncludeFacilities;

        // Issue rows each block owns: the readers skip them with the block.
        internal static readonly string[] InventoryIssues = { "inventory_weapons", "inventory_item_count", "carried_thing_id" };
        internal static readonly string[] GearDetailIssueSuffixes = { ".armor_sharp", ".armor_blunt", ".insulation_cold", ".insulation_heat" };
        internal static readonly string[] BackstoryIssues = { "biological_age_years", "chronological_age_years", "childhood", "adulthood", "childhood.label", "adulthood.label", "title", "title_source" };

        internal static Obs.PawnSnapshot Apply(Obs.PawnSnapshot snapshot, Obs.PawnFields? mask)
        {
            if (mask == null) return snapshot;
            foreach (var row in snapshot.Pawns)
            {
                if (row.Equipment != null)
                {
                    if (!Inventory(mask))
                    {
                        row.Equipment.InventoryWeapons.Clear(); row.Equipment.ClearCarriedThingId(); row.Equipment.ClearInventoryItemCount();
                        Drop(row.Equipment.Issues, f => InventoryIssues.Contains(f) || f.StartsWith("inventory_weapons."));
                    }
                    if (!GearDetail(mask))
                    {
                        foreach (var item in row.Equipment.Equipped) StripGear(item);
                        foreach (var item in row.Equipment.Apparel) StripGear(item);
                        foreach (var item in row.Equipment.InventoryWeapons) StripGear(item);
                        Drop(row.Equipment.Issues, f => GearDetailIssueSuffixes.Any(f.EndsWith));
                    }
                }
                if (row.Health != null)
                {
                    if (!Capacities(mask)) { row.Health.Capacities.Clear(); Drop(row.Health.Issues, f => f == "capacities"); }
                    if (!SurgeryBills(mask)) { row.Health.SurgeryBills.Clear(); Drop(row.Health.Issues, f => f == "surgery_bills"); }
                }
                if (row.Biography != null)
                {
                    if (!Backstory(mask))
                    {
                        row.Biography.Childhood = null; row.Biography.Adulthood = null;
                        row.Biography.ClearTitle(); row.Biography.ClearTitleSource();
                        row.Biography.ClearBiologicalAgeYears(); row.Biography.ClearChronologicalAgeYears();
                        Drop(row.Biography.Issues, f => BackstoryIssues.Contains(f));
                    }
                    if (!Traits(mask)) { row.Biography.Traits.Clear(); Drop(row.Biography.Issues, f => f == "traits"); }
                }
                if (row.Social != null && !Relations(mask)) row.Social.Relations.Clear();
            }
            return snapshot;
        }

        private static void Drop(RepeatedField<Obs.ReadIssue> issues, System.Func<string, bool> owned)
        {
            foreach (var issue in issues.Where(i => owned(i.Field)).ToArray()) issues.Remove(issue);
        }

        private static void StripGear(Obs.GearItem item)
        {
            item.ClearStuff(); item.ClearQuality(); item.ClearHitPoints(); item.ClearMaxHitPoints();
            item.ApparelLayers.Clear(); item.ClearArmorSharp(); item.ClearArmorBlunt();
            item.ClearInsulationCold(); item.ClearInsulationHeat();
        }

        internal static Obs.PopulationSnapshot Apply(Obs.PopulationSnapshot snapshot, Obs.PopulationFields? mask)
        {
            if (mask == null) return snapshot;
            foreach (var person in snapshot.Persons)
            {
                if (!OwnedBed(mask)) person.OwnedBed = null;
                if (!Nutrition(mask)) person.ClearNutritionPerDay();
            }
            if (!SupportedInteractions(mask)) snapshot.SupportedInteractions.Clear();
            return snapshot;
        }

        internal static Obs.ResearchSnapshot Apply(Obs.ResearchSnapshot snapshot, Obs.ResearchFields? mask)
        {
            if (mask == null) return snapshot;
            foreach (var project in snapshot.Projects)
            {
                if (!Unlocks(mask)) { project.Unlocks.Clear(); project.UnlocksCompleteness = null; }
                if (!Costs(mask))
                {
                    project.ClearBaseCost(); project.ClearApparentCost(); project.ClearCostFactor();
                    project.ClearProgress(); project.ClearProgressFraction();
                    project.ClearTechprintsApplied(); project.ClearTechprintsNeeded();
                    Drop(project.Issues, f => f == "progress_fraction");
                }
                if (!Facilities(mask)) project.RequiredFacilities.Clear();
            }
            return snapshot;
        }
    }
}
