package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RemoveBlight cuts blighted crops before the blight spreads through the
// colony's growing zones. The goal opens while the native
// blighted-plant census (the planning window's plant things) is non-empty
// and settles when it is empty again: a cut designation is the method, not
// the outcome, so a receipt never settles it. It is a Standard whose target
// is no outstanding work.
const RemoveBlight ConcernID = "RemoveBlight"

// BlightedPlant is one native census row: a blighted plant standing in a
// player growing zone or on home ground. Designated is native's own record
// of an existing cut or harvest designation; Eligible is whether native
// offers the plant for a CutPlant designation.
type BlightedPlant struct {
	ID         string
	Definition string
	Cell       domain.Cell
	Zone       string
	Designated bool
	Eligible   bool
}

// BlightDeficit is the binary RemoveBlight deficit signal: an unknown census
// stays unknown (absence is never evidence of recovery); otherwise deficit is
// simply "any blighted plant still stands", designated or not, because the
// goal settles on the cut, not on the order.
func BlightDeficit(plants domain.Fact[[]BlightedPlant]) domain.Fact[bool] {
	rows, known := plants.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(rows) > 0)
}

// SelectBlightCuts is the plants RemoveBlight designates this cycle: every
// undesignated census row not already claimed by an open admission, lowest
// plant ID first, at most limit rows. A designated plant is left to the
// work it already has; a claimed one to the admission that holds it.
func SelectBlightCuts(plants []BlightedPlant, claimed map[string]bool, limit int) []BlightedPlant {
	var out []BlightedPlant
	for _, plant := range plants {
		if plant.ID == "" || !plant.Eligible || plant.Designated || claimed[plant.ID] {
			continue
		}
		out = append(out, plant)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	if limit > 0 && len(out) > limit {
		out = out[:limit]
	}
	return out
}
