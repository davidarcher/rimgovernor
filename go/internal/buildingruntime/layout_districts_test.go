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

func TestLayoutAnchorReadsTheLayoutPlan(t *testing.T) {
	p := districtProjection(policy.BuildTierMasonry)
	p.LayoutPlan = domain.Known(policy.LayoutPlan{
		Rooms: []policy.LayoutRoom{
			{Role: policy.ModuleBarracks, Interior: policy.Rectangle{X: 10, Z: 10, Width: 5, Height: 5}},
			{Role: policy.ModuleBarracks, Interior: policy.Rectangle{X: 20, Z: 10, Width: 5, Height: 5}},
			{Role: policy.ModuleStorage, Interior: policy.Rectangle{X: 30, Z: 10, Width: 4, Height: 4}},
		},
		Zones: []policy.LayoutZone{{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 70, X: 60, Length: 10}}}},
	})
	if c := layoutAnchor(p, policy.DistrictHousing); c != (domain.Cell{X: 12, Z: 12}) {
		t.Fatalf("housing %v", c)
	}
	if c := layoutAnchor(p, policy.DistrictStorage); c != (domain.Cell{X: 32, Z: 12}) {
		t.Fatalf("storage %v", c)
	}
	if c := layoutAnchor(p, policy.DistrictFields); c != (domain.Cell{X: 65, Z: 70}) {
		t.Fatalf("fields %v", c)
	}
	// A wall in the first barracks moves housing to the second.
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 11, Z: 11}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	p.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "w", Building: wall, Cells: []domain.Cell{{X: 11, Z: 11}}}}})
	if c := layoutAnchor(p, policy.DistrictHousing); c != (domain.Cell{X: 22, Z: 12}) {
		t.Fatalf("next housing %v", c)
	}
	// Production has no planned room here: the colony centre anchors it.
	if c := layoutAnchor(p, policy.DistrictProduction); c != p.Center {
		t.Fatalf("production %v", c)
	}
	// Camp ignores the plan.
	p.BuildTier = domain.Known(policy.BuildTierCamp)
	if layoutAnchor(p, policy.DistrictHousing) != p.Center {
		t.Fatal("camp anchors on the centre")
	}
}

func TestLayoutAnchorWithoutAPlanIsTheCentre(t *testing.T) {
	p := districtProjection(policy.BuildTierMasonry)
	for _, d := range []policy.District{policy.DistrictHousing, policy.DistrictProduction, policy.DistrictFields} {
		if layoutAnchor(p, d) != p.Center {
			t.Fatalf("%s anchors off the centre without a plan", d)
		}
	}
}
