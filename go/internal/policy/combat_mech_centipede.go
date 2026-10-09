package policy

import "strings"

// firingGap is how many empty tiles Formation keeps between firing cells:
// two against a live centipede or any explosive weapon,
// whose area blasts hit packed shooters together, else one.
func firingGap(view CombatView) int32 {
	for _, h := range rankThreats(view) {
		if strings.HasPrefix(h.Kind, "Mech_Centipede") || threatTier(h, nil) == threatExplosive {
			return 2
		}
	}
	return 1
}
