namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The construction tier of every planned room role (#2524, ladder in
    /// docs/developers/contracts/construction-tiers.md). Tier orders construction only; it is not the tech
    /// ladder. Unlisted rooms are Expand by decision; the armory and wardrobe equip the defense and are Secure (#2527).
    /// </summary>
    internal static readonly Dictionary<PlannedRole, ConstructionTier> PlannedRoleTier = new()
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
        [PlannedRole.Pen] = ConstructionTier.Comfort,

        [PlannedRole.Workshop] = ConstructionTier.Produce,

        [PlannedRole.TrainingRange] = ConstructionTier.Expand,
        [PlannedRole.SparringRing] = ConstructionTier.Expand,
        [PlannedRole.Yard] = ConstructionTier.Expand,
        [PlannedRole.Tomb] = ConstructionTier.Expand,
        [PlannedRole.Morgue] = ConstructionTier.Expand,
        [PlannedRole.Prison] = ConstructionTier.Expand,
        [PlannedRole.Reserve] = ConstructionTier.Expand,
        [PlannedRole.Circulation] = ConstructionTier.Expand,
        [PlannedRole.Battery] = ConstructionTier.Expand,
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
        // The shelter family's starter shelter and spare room are Survive; bed ownership and bedrooms Sustain.
        if (concern == ShelterFamily.Starter.FilingKey || concern == ShelterFamily.Expansion.FilingKey)
        {
            return ConstructionTier.Survive;
        }
        if (concern == ShelterFamily.Sleeping.FilingKey)
        {
            return ConstructionTier.Sustain;
        }
        if (concern == CookingNeed.FilingKey || concern == EconomyFamily.Research.FilingKey)
        {
            return ConstructionTier.Survive;
        }
        // The food/supply Need builds only the butcher facility.
        if (concern == FoodSupplyNeed.FilingKey || concern == MedicalRules.MedicalCareNeed.FilingKey || concern == RefrigerationNeed.FilingKey
            || concern == AdminFamily.Stockpiles.FilingKey)
        {
            return ConstructionTier.Sustain;
        }
        if (concern == LayoutFamily.Defense.FilingKey)
        {
            return ConstructionTier.Secure;
        }
        if (RoomsFamily.ComfortStep(concern))
        {
            return ConstructionTier.Comfort;
        }
        if (concern == EconomyFamily.Resource.FilingKey)
        {
            return ConstructionTier.Produce;
        }
        return ConstructionTier.Expand;
    }
}
