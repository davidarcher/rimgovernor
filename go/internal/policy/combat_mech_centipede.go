package policy

import "strings"

// firingGap is how many empty tiles Formation keeps between firing cells:
// two against a live centipede (#925) or any explosive weapon (#1054),
// whose area blasts hit packed shooters together, else one (#861).
func firingGap(view CombatView) int32 {
	for _, h := range rankThreats(view) {
		if strings.HasPrefix(h.Kind, "Mech_Centipede") || threatTier(h, nil) == threatExplosive {
			return 2
		}
	}
	return 1
}
