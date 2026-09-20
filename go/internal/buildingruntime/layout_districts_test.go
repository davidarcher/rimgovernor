package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestShelterStylePicksByTierAndFaction(t *testing.T) {
	cases := []struct {
		name  string
		tier  domain.Fact[policy.BuildTier]
		level domain.Fact[string]
		want  policy.ShelterStyle
	}{
		{"camp neolithic", domain.Known(policy.BuildTierCamp), domain.Known("Neolithic"), policy.ShelterHut},
		{"camp medieval", domain.Known(policy.BuildTierCamp), domain.Known("Medieval"), policy.ShelterRectangle},
		{"unknown tier neolithic", domain.Unknown[policy.BuildTier](), domain.Known("Neolithic"), policy.ShelterHut},
		{"unknown everything", domain.Unknown[policy.BuildTier](), domain.Unknown[string](), policy.ShelterRectangle},
		{"masonry neolithic", domain.Known(policy.BuildTierMasonry), domain.Known("Neolithic"), policy.ShelterModule},
		{"powered", domain.Known(policy.BuildTierMasonry + 1), domain.Known("Industrial"), policy.ShelterModule},
	}
	for _, c := range cases {
		facts := observation.ColonyProjection{BuildTier: c.tier, PlayerTechLevel: c.level}
		if got := shelterStyle(facts); got != c.want {
			t.Fatalf("%s: style %s, want %s", c.name, got, c.want)
		}
	}
}

func TestPlannerDistrictFollowsTheFacilityRole(t *testing.T) {
	shelter := &RoutineBuildingPlanner{shelter: true}
	if shelter.district() != policy.DistrictHousing {
		t.Fatal("the shelter planner raises housing")
	}
	workshop, err := policy.Facility(policy.RoomRoleWorkshop)
	if err != nil {
		t.Fatal(err)
	}
	if (&RoutineBuildingPlanner{facility: &workshop}).district() != policy.DistrictProduction {
		t.Fatal("a workshop ladder sites in production")
	}
	bedroom, err := policy.Facility(policy.RoomRoleBedroom)
	if err != nil {
		t.Fatal(err)
	}
	if (&RoutineBuildingPlanner{facility: &bedroom}).district() != policy.DistrictHousing {
		t.Fatal("a bedroom ladder sites in housing")
	}
}

// districtProjection is an open 96x96 map at the tier with the grid fixed
// on (40,40); every cell is observed open ground.
func districtProjection(tier policy.BuildTier) observation.ColonyProjection {
	grid := policy.ColonyGrid{Origin: domain.Cell{X: 40, Z: 40}, Pitch: policy.GridPitch, Axes: policy.ColonyGridAxes, Source: policy.ColonyGridFromStarter}
	p := observation.ColonyProjection{BuildTier: domain.Known(tier), ColonyGrid: domain.Known(grid), Bounds: policy.Bounds{Width: 96, Height: 96}, Center: domain.Cell{X: 46, Z: 46}}
	for x := int32(0); x < 96; x++ {
		for z := int32(0); z < 96; z++ {
			p.Cells = append(p.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)})
		}
	}
	return p
}

func TestLayoutAnchorSitesEachDistrictOrFallsBack(t *testing.T) {
	p := districtProjection(policy.BuildTierMasonry)
	housing := layoutAnchor(p, policy.RoomDistrict(policy.RoomRoleBedroom))
	production := layoutAnchor(p, policy.RoomDistrict(policy.RoomRoleWorkshop))
	if housing != (domain.Cell{X: 46, Z: 62}) || production != (domain.Cell{X: 62, Z: 46}) {
		t.Fatalf("housing %v production %v", housing, production)
	}
	g, _ := p.ColonyGrid.Value()
	if g.District(housing) != policy.DistrictHousing || g.District(production) != policy.DistrictProduction {
		t.Fatal("anchors outside their districts")
	}
	// A wall on the nearest housing module moves the anchor to the next
	// housing module; the plaza's own module never anchors a wedge.
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 46, Z: 60}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	p.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "w", Building: wall, Cells: []domain.Cell{{X: 46, Z: 60}}}}})
	next := layoutAnchor(p, policy.DistrictHousing)
	if next == housing || g.District(next) != policy.DistrictHousing {
		t.Fatalf("next housing anchor %v", next)
	}
	// Camp, an unknown grid, and a district with no observed free module
	// all anchor on the colony centre.
	if layoutAnchor(districtProjection(policy.BuildTierCamp), policy.DistrictHousing) != p.Center {
		t.Fatal("camp anchors on the centre")
	}
	blind := districtProjection(policy.BuildTierMasonry)
	blind.ColonyGrid = domain.Unknown[policy.ColonyGrid]()
	if layoutAnchor(blind, policy.DistrictHousing) != p.Center {
		t.Fatal("no grid anchors on the centre")
	}
	full := districtProjection(policy.BuildTierMasonry)
	full.Cells = nil
	if layoutAnchor(full, policy.DistrictHousing) != p.Center {
		t.Fatal("a full district anchors on the centre")
	}
}
