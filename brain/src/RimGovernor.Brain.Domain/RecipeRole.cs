namespace RimGovernor.Brain.Domain;

/// <summary>
/// What a recipe does, read from its RecipeDef row rather than its defName: the catalog derives it, facts and
/// bills carry it. Go <c>RecipeRole</c>; the <c>Role</c> prefix of the members is dropped.
/// </summary>
public enum RecipeRole
{
    /// <summary>A recipe with no role the planners single out (Go <c>RoleNone</c>).</summary>
    [WireName("")]
    None = 0,

    /// <summary>
    /// Butchers corpses for their meat (the recipe whose worker counter is RecipeWorkerCounter_ButcherAnimals).
    /// </summary>
    ButcherFlesh,

    /// <summary>
    /// Cooks a perishable meal of the simple, fine or lavish kind; a meal that never rots is a reserve, not
    /// part of the tier family.
    /// </summary>
    OrdinaryMeal,

    /// <summary>Makes an art building (a product carrying CompProperties_Art).</summary>
    Sculpture,
}

public static class RecipeRoleExtensions
{
    /// <summary>Go <c>RecipeRole.Valid</c>: whether the role is one the catalog derives or none.</summary>
    public static bool IsValid(this RecipeRole role) => Wire.IsDefined(role);
}
