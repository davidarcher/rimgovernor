package policy

import "slices"

// FoodRestriction separates the saved whitelist from native diet eligibility.
// It is observation, never an explicit controller preference.
type FoodRestriction struct {
	PolicyID          string
	Allowed, Eligible []string
}

// FoodPolicyChanges restores natively suitable foods without changing the
// pawn's special ingredient filters or condition ranges. A missing census or
// unavailable pawn does not authorize a write. Larger mod sets converge in
// bounded batches.
func FoodPolicyChanges(pawn WorkPawn) []string {
	available, ok := pawn.Available.Value()
	if !ok || !available {
		return nil
	}
	diet, ok := pawn.FoodRestriction.Value()
	if !ok {
		return nil
	}
	var defs []string
	for _, def := range diet.Eligible {
		if !slices.Contains(diet.Allowed, def) {
			defs = append(defs, def)
		}
	}
	slices.Sort(defs)
	defs = slices.Compact(defs)
	if len(defs) > 256 {
		defs = defs[:256]
	}
	return defs
}
