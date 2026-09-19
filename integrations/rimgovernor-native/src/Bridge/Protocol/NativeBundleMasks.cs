#nullable enable
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Field masks for the bundle's continuous families (issue #360). A mask
    /// keeps only the sub-blocks it includes: every other block the mask can
    /// name is cleared from the bundle's copy of the family, with no issue
    /// row, since the controller asked for its absence. An absent mask keeps
    /// the family exactly as the dedicated read returns it. The reads
    /// themselves are untouched: the dedicated tools (ListPawns,
    /// ReadPopulation, ReadResearch) never see a mask.
    /// </summary>
    internal static class NativeBundleMasks
    {
        internal static Obs.PawnSnapshot Apply(Obs.PawnSnapshot snapshot, Obs.PawnFields? mask)
        {
            if (mask == null) return snapshot;
            foreach (var row in snapshot.Pawns)
            {
                if (row.Equipment != null)
                {
                    if (!mask.IncludeInventory) { row.Equipment.InventoryWeapons.Clear(); row.Equipment.ClearCarriedThingId(); row.Equipment.ClearInventoryItemCount(); }
                    if (!mask.IncludeGearDetail)
                    {
                        foreach (var item in row.Equipment.Equipped) StripGear(item);
                        foreach (var item in row.Equipment.Apparel) StripGear(item);
                        foreach (var item in row.Equipment.InventoryWeapons) StripGear(item);
                    }
                }
                if (row.Health != null)
                {
                    if (!mask.IncludeCapacities) row.Health.Capacities.Clear();
                    if (!mask.IncludeSurgeryBills) row.Health.SurgeryBills.Clear();
                }
                if (row.Biography != null)
                {
                    if (!mask.IncludeBackstory)
                    {
                        row.Biography.Childhood = null; row.Biography.Adulthood = null;
                        row.Biography.ClearTitle(); row.Biography.ClearTitleSource();
                        row.Biography.ClearBiologicalAgeYears(); row.Biography.ClearChronologicalAgeYears();
                    }
                    if (!mask.IncludeTraits) row.Biography.Traits.Clear();
                }
                if (row.Social != null && !mask.IncludeRelations) row.Social.Relations.Clear();
            }
            return snapshot;
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
                if (!mask.IncludeOwnedBed) person.OwnedBed = null;
                if (!mask.IncludeNutrition) person.ClearNutritionPerDay();
            }
            if (!mask.IncludeSupportedInteractions) snapshot.SupportedInteractions.Clear();
            return snapshot;
        }

        internal static Obs.ResearchSnapshot Apply(Obs.ResearchSnapshot snapshot, Obs.ResearchFields? mask)
        {
            if (mask == null) return snapshot;
            foreach (var project in snapshot.Projects)
            {
                if (!mask.IncludeUnlocks) { project.Unlocks.Clear(); project.UnlocksCompleteness = null; }
                if (!mask.IncludeCosts)
                {
                    project.ClearBaseCost(); project.ClearApparentCost(); project.ClearCostFactor();
                    project.ClearProgress(); project.ClearProgressFraction();
                    project.ClearTechprintsApplied(); project.ClearTechprintsNeeded();
                }
                if (!mask.IncludeFacilities) project.RequiredFacilities.Clear();
            }
            return snapshot;
        }
    }
}
