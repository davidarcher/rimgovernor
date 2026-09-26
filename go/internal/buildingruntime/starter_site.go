package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// StarterSite is the 9x9 rectangle the initial shelter's starter search
// ranks first on facts (#700): the same anchor, protected ground and site
// cells previewShell searches, natural rock on its ring reused and rock
// inside mined. Acceptance fixtures stage their hut on it so a case starts
// from the shell the controller would have raised, not the nearest clean
// square. ok is false when no rectangle fits.
func StarterSite(facts observation.ColonyProjection) (layout policy.StarterLayout, ok bool, err error) {
	grid, _ := layoutAlignment(facts)
	layouts, err := policy.StarterLayouts(policy.StarterRequest{Bounds: facts.Bounds, Anchor: layoutAnchor(facts, policy.RoomDistrict(policy.RoomRoleBarracks)), Cells: shellSiteCells(facts, nil), Protected: layoutProtected(facts, nil), Shelter: policy.ShelterRectangle, Grid: grid})
	if err != nil {
		return policy.StarterLayout{}, false, err
	}
	// The rectangle search falls back to concave and grown shapes where no
	// rectangle fits; a fixture hut is only ever the square.
	for _, l := range layouts {
		if l.Room.Width == 9 && l.Room.Height == 9 {
			return l, true, nil
		}
	}
	return policy.StarterLayout{}, false, nil
}
