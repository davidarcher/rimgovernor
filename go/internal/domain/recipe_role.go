package domain

// RecipeRole is what a recipe does, read from its RecipeDef row rather than
// its defName (#1721): the catalog derives it, facts and bills carry it.
type RecipeRole string

const (
	// RoleNone is a recipe with no role the planners single out.
	RoleNone RecipeRole = ""
	// RoleButcherFlesh butchers corpses for their meat (the recipe whose worker
	// counter is RecipeWorkerCounter_ButcherAnimals).
	RoleButcherFlesh RecipeRole = "butcher_flesh"
	// RoleCremation destroys corpses: it consumes corpses and makes nothing.
	RoleCremation RecipeRole = "cremation"
	// RoleOrdinaryMeal cooks a perishable meal of the simple, fine or lavish
	// kind; a meal that never rots is a reserve, not part of the tier family.
	RoleOrdinaryMeal RecipeRole = "ordinary_meal"
	// RoleSculpture makes an art building (a product carrying CompProperties_Art).
	RoleSculpture RecipeRole = "sculpture"
)

// Valid reports whether r is a role the catalog derives or none.
func (r RecipeRole) Valid() bool {
	switch r {
	case RoleNone, RoleButcherFlesh, RoleCremation, RoleOrdinaryMeal, RoleSculpture:
		return true
	}
	return false
}
