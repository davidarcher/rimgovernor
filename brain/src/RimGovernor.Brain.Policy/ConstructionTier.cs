namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The construction tier of every planned room role (#2524, ladder in
    /// docs/developers/contracts/construction-tiers.md). Tier orders construction only; it is not the tech
    /// ladder. Unlisted rooms are Expand by decision; the armory and wardrobe equip the defense and are Secure (#2527).
    /// </summary>
    private static readonly Dictionary<PlannedRole, ConstructionTier> PlannedRoleTier = new()
    {
        [PlannedRole.Shelter] = ConstructionTier.Survive,
        [PlannedRole.Airlock] = ConstructionTier.Survive,
        [PlannedRole.Porch] = ConstructionTier.Survive,
        [PlannedRole.Kitchen] = ConstructionTier.Survive,

        [PlannedRole.Bedroom] = ConstructionTier.Sustain,
        [PlannedRole.Hospital] = ConstructionTier.Sustain,
        [PlannedRole.Butchery] = ConstructionTier.Sustain,
        [PlannedRole.Storage] = ConstructionTier.Sustain,
        [PlannedRole.MealCloset] = ConstructionTier.Sustain,
        [PlannedRole.Lab] = ConstructionTier.Sustain,
        [PlannedRole.Freezer] = ConstructionTier.Sustain,

        [PlannedRole.Dining] = ConstructionTier.Comfort,
        [PlannedRole.Rec] = ConstructionTier.Comfort,
        [PlannedRole.Throne] = ConstructionTier.Comfort,
        [PlannedRole.Worship] = ConstructionTier.Comfort,
        [PlannedRole.Suite] = ConstructionTier.Comfort,
        [PlannedRole.Barn] = ConstructionTier.Comfort,
        [PlannedRole.Pen] = ConstructionTier.Comfort,

        [PlannedRole.Workshop] = ConstructionTier.Produce,

        [PlannedRole.Graveyard] = ConstructionTier.Expand,
        [PlannedRole.TrainingRange] = ConstructionTier.Expand,
        [PlannedRole.SparringRing] = ConstructionTier.Expand,
        [PlannedRole.WasteYard] = ConstructionTier.Expand,
        [PlannedRole.Yard] = ConstructionTier.Expand,
        [PlannedRole.Tomb] = ConstructionTier.Expand,
        [PlannedRole.Morgue] = ConstructionTier.Expand,
        [PlannedRole.Incinerator] = ConstructionTier.Expand,
        [PlannedRole.Prison] = ConstructionTier.Expand,
        [PlannedRole.Reserve] = ConstructionTier.Expand,
        [PlannedRole.Circulation] = ConstructionTier.Expand,
        [PlannedRole.Battery] = ConstructionTier.Expand,
        [PlannedRole.Armory] = ConstructionTier.Secure,
        [PlannedRole.Wardrobe] = ConstructionTier.Secure,
        [PlannedRole.Nursery] = ConstructionTier.Expand,
        [PlannedRole.Playroom] = ConstructionTier.Expand,
        [PlannedRole.Classroom] = ConstructionTier.Expand,
        [PlannedRole.DeathrestChamber] = ConstructionTier.Expand,
        [PlannedRole.ContainmentCell] = ConstructionTier.Expand,
        [PlannedRole.IsolationRoom] = ConstructionTier.Expand,
    };

    /// <summary>The tier a planned room's builds carry: its shell, floors and furniture. A role missing from the table is Expand.</summary>
    public static ConstructionTier RoomTier(PlannedRole role) =>
        PlannedRoleTier.TryGetValue(role, out var tier) ? tier : ConstructionTier.Expand;

    /// <summary>
    /// The tier the finishing-skill adoption of an existing site restates: the tier native already reads on
    /// it, so the adoption never moves the build. A site with no tier (placed before tiers, or by hand) is
    /// unlisted, so Expand.
    /// </summary>
    public static ConstructionTier AdoptedTier(ConstructionSite site) =>
        site.Tier.TryGetValue(out var tier) ? tier : ConstructionTier.Expand;

    /// <summary>
    /// The tier of a loose building a Concern's planner places outside any planned room. Every concern has a
    /// tier: a concern the ladder does not list is Expand by decision. The defense concerns are Secure (#2527;
    /// promotion is DefenseBuildTier). Power takes the tier of what it serves: the refrigeration planner's
    /// builds stay Sustain, and the generic power planner proposes no consumer, so it is Expand.
    /// </summary>
    public static ConstructionTier PlannerTier(ConcernId concern, Phase phase)
    {
        if (concern == MaintainHousing)
        {
            return phase == Phase.HousingSleeping ? ConstructionTier.Sustain : ConstructionTier.Survive;
        }
        if (concern == EnsureCooking || concern == EnsureResearch)
        {
            return ConstructionTier.Survive;
        }
        // EnsureFoodSupply builds only the butcher facility.
        if (concern == EnsureFoodSupply || concern == MaintainMedicalReserves || concern == MaintainRefrigeration
            || concern == MaintainStockpiles || concern == MaintainFoodStorage)
        {
            return ConstructionTier.Sustain;
        }
        if (concern == EnsureBasicDefense || concern == EnsureDefensiveLayout)
        {
            return ConstructionTier.Secure;
        }
        if (concern == EnsureComfort || concern == MaintainAnimalContainment)
        {
            return ConstructionTier.Comfort;
        }
        if (concern == MaintainResource)
        {
            return ConstructionTier.Produce;
        }
        return ConstructionTier.Expand;
    }
}
