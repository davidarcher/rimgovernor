package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ringClaims builds the Wall/Door claims of a w x h rectangle's ring with a
// south door at its centre column, as a layout-plan room builder raises it.
func ringClaims(t *testing.T, plan domain.PlanID, x0, z0, w, h int32) []policy.ConstructionClaim {
	t.Helper()
	var claims []policy.ConstructionClaim
	for x := x0; x < x0+w; x++ {
		for z := z0; z < z0+h; z++ {
			if x != x0 && x != x0+w-1 && z != z0 && z != z0+h-1 {
				continue
			}
			def := "Wall"
			if z == z0 && x == x0+w/2 {
				def = "Door"
			}
			b, err := domain.NewBuilding(def, domain.Cell{X: x, Z: z}, domain.North, "")
			if err != nil {
				t.Fatal(err)
			}
			claims = append(claims, policy.ConstructionClaim{Plan: plan, Building: b})
		}
	}
	return claims
}

// The live 11x11 layout-plan shelter (door (119,128), bounds 114..124 x
// 128..138) matched no starter template, so MaintainFoodStorage never found a
// room and the colony had no food stockpile while fish rotted outside.
func TestStarterRoomRecoversLayoutPlanRectangle(t *testing.T) {
	claims := ringClaims(t, "routine-shell-x", 114, 128, 11, 11)
	if len(claims) != 40 {
		t.Fatal(len(claims))
	}
	room, known := starterRoom(domain.Known(claims))
	if !known || room != (policy.Rectangle{X: 114, Z: 128, Width: 11, Height: 11}) {
		t.Fatal(room, known)
	}
	if _, known := starterRoom(domain.Known(claims[:39])); known {
		t.Fatal("broken ring recognized as a room")
	}
	var walls []policy.ConstructionClaim
	for _, c := range claims {
		if c.Building.Definition() == "Wall" {
			walls = append(walls, c)
		}
	}
	if _, known := starterRoom(domain.Known(walls)); known {
		t.Fatal("doorless ring recognized as a room")
	}
	cells := map[domain.Cell]policy.SiteCell{}
	for x := int32(115); x < 124; x++ {
		for z := int32(129); z < 138; z++ {
			c := domain.Cell{X: x, Z: z}
			cells[c] = policy.SiteCell{Cell: c, Indoors: domain.Known(true), Roofed: domain.Known(true), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)}
		}
	}
	if sites := foodStorageSites(room, cells, nil); len(sites) == 0 || len(sites[0]) != 9 {
		t.Fatal(sites)
	}
}
