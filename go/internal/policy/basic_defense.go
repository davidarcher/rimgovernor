package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// basicDefenseRecovered is the Foothold defense gate (two armed, no
// hostiles) plus every fighting-capable colonist holding a weapon: a club
// left on the ground while a colonist stands unarmed keeps the goal owed so
// the equip planner arms them, or crafts a weapon when none lies loose. The
// Foothold gate itself stays at two armed; an unknown unarmed count defers
// to it.
func basicDefenseRecovered(gate domain.Fact[bool], unarmed domain.Fact[int64]) domain.Fact[bool] {
	if n, known := unarmed.Value(); known && n > 0 {
		if _, gk := gate.Value(); gk {
			return domain.Known(false)
		}
	}
	return gate
}
