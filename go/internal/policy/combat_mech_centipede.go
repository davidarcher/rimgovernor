package policy

import "strings"

// firingGap is how many empty tiles Formation keeps between firing cells:
// two against a live centipede (#925), whose area and inferno blasts hit
// packed shooters together, else one (#861).
func firingGap(view CombatView) int32 {
	for _, h := range rankThreats(view) {
		if strings.HasPrefix(h.Kind, "Mech_Centipede") {
			return 2
		}
	}
	return 1
}
