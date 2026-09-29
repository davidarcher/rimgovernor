package startersite

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// plannedSite is the size x size fixture hut on the layout plan's first
// planned storeroom (#1250): the square shares the storeroom ring's
// south-west corner, pulled back inside the map, and keeps the planned
// door when it lands on the square's ring off a corner (else the hut's
// mid east wall). ok is false when the plan holds no storeroom.
func plannedSite(plan policy.LayoutPlan, bounds policy.Bounds, size int32) (site, door domain.Cell, ok bool) {
	shells := plan.PlannedShells(policy.RoomRoleStoreroom)
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
