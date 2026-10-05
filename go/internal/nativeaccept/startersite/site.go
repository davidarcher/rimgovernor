package startersite

import (
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// plannedSite is the size x size fixture hut on the layout plan's shelter
// room (#1250, #2048): the square shares the shelter ring's south-west
// corner, pulled back inside the map, and keeps the planned door when it
// lands on the square's ring off a corner (else the hut's mid east wall).
// ok is false when the plan holds no shelter.
func plannedSite(plan policy.LayoutPlan, bounds policy.Bounds, size int32) (site, door domain.Cell, ok bool) {
	shells := plan.PlannedShells(policy.RoomRoleShelter)
	if len(shells) == 0 || bounds.Width < size || bounds.Height < size {
		return domain.Cell{}, domain.Cell{}, false
	}
	b := shells[0].Bounds()
	site = domain.Cell{X: min(max(b.X, 0), bounds.Width-size), Z: min(max(b.Z, 0), bounds.Height-size)}
	d := shells[0].Door()
	minX, minZ, maxX, maxZ := site.X, site.Z, site.X+size-1, site.Z+size-1
	edgeX, edgeZ := d.X == minX || d.X == maxX, d.Z == minZ || d.Z == maxZ
	inside := d.X >= minX && d.X <= maxX && d.Z >= minZ && d.Z <= maxZ
	if !inside || edgeX == edgeZ {
		d = domain.Cell{X: maxX, Z: site.Z + size/2}
	}
	return site, d, true
}

// plannedBedrooms is the plan's bedroom-wing rooms as the fixture's
// bedrooms argument: "x,z,width,height,doorX,doorZ" per room (its
// interior and door cell), joined by ';'.
func plannedBedrooms(plan policy.LayoutPlan) string {
	var rooms []string
	for _, r := range plan.AllRooms() {
		if r.Role != policy.PlannedBedroom {
			continue
		}
		in := r.Interior
		rooms = append(rooms, fmt.Sprintf("%d,%d,%d,%d,%d,%d", in.X, in.Z, in.Width, in.Height, r.Door.X, r.Door.Z))
	}
	return strings.Join(rooms, ";")
}
