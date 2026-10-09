package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestColdMapCurve(t *testing.T) {
	t.Parallel()
	warm := []float64{4, 6, 9, 13, 17, 21, 24, 23, 19, 14, 9, 5}
	frozen := []float64{-12, -9, -3, 3, 8, 12, 15, 14, 9, 2, -5, -10}
	edge := append([]float64(nil), warm...)
	edge[0] = 0
	if ColdMapCurve(nil) || ColdMapCurve(warm) || ColdMapCurve(edge) {
		t.Fatal("a curve that stays at or above freezing is cold")
	}
	if !ColdMapCurve(frozen) {
		t.Fatal("a curve that dips below freezing is not cold")
	}
	if ShelterCampfires(true) != 2 || ShelterCampfires(false) != 0 {
		t.Fatalf("campfires cold=%d warm=%d, want 2 and 0", ShelterCampfires(true), ShelterCampfires(false))
	}
}

// The cold flag reaches the generator on the plan: a cold map's shelter is
// sized for its two campfires, a normal map's for none, and a later generate
// over the plan keeps the shelter and the latch (#2044).
func TestShelterIsSizedByTheLatchedClimate(t *testing.T) {
	t.Parallel()
	zones := coreTestZones()
	g := newCoreGrid(zones, nil)
	seed, ok := g.seed()
	if !ok {
		t.Fatal("no seed")
	}
	cold := g.generate(LayoutPlan{Zones: zones, Cold: true}, seed, 3, 0, TechTierCamp)
	warm := g.generate(LayoutPlan{Zones: zones}, seed, 3, 0, TechTierCamp)
	if !cold.Cold || warm.Cold {
		t.Fatalf("latch cold=%v warm=%v", cold.Cold, warm.Cold)
	}
	for _, c := range []struct {
		name string
		plan LayoutPlan
		cold bool
	}{{"cold", cold, true}, {"warm", warm, false}} {
		got, want := shelterOf(t, c.plan).Interior, ShelterSizes(3, ShelterCampfires(c.cold), 0)[0]
		if got.Width != want[0] || got.Height != want[1] {
			t.Errorf("%s shelter %dx%d, want %v", c.name, got.Width, got.Height, want)
		}
	}
	if ShelterInteriorArea(3, ShelterCampfires(true), 0) <= ShelterInteriorArea(3, ShelterCampfires(false), 0) {
		t.Fatal("a cold shelter is no bigger than a normal one")
	}
	before := shelterOf(t, cold)
	grown := growPlan(cold, 12, 1, TechTierCamp)
	if !grown.Cold || !before.Same(shelterOf(t, grown)) {
		t.Fatal("a replan resized or re-sited the cold shelter, or lost the latch")
	}
}

// On a hot map the shelter is a sleeping room the base-wide temperature
// planner serves like any other (#2044): it picks the powered cooler through
// a vented wall of the planned shelter, or the passive cooler on its floor
// when no power can run one. The shelter template reserves no cell for it.
func TestTemperaturePlannerCoolsTheShelter(t *testing.T) {
	t.Parallel()
	plan := corePlan(coreTestZones(), 3, TechTierCamp)
	shelter := shelterOf(t, plan)
	in := shelter.Interior
	walls := pad(in, 1)
	room := Room{ID: "shelter", Beds: []string{"bed1", "bed2", "bed3"}, Temperature: domain.Known(36.0), Enclosed: domain.Known(true), Contents: domain.Known([]Amount{})}
	var cells []SiteCell
	for x := walls.X - 2; x < walls.X+walls.Width+2; x++ {
		for z := walls.Z - 2; z < walls.Z+walls.Height+2; z++ {
			c := domain.Cell{X: x, Z: z}
			switch {
			case rectContains(in, c):
				room.Cells = append(room.Cells, c)
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Things: OccupantThings(false), Indoors: domain.Known(true), Roofed: domain.Known(true)})
			case c == shelter.Door:
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Things: OccupantThings(true), Indoors: domain.Known(false), Roofed: domain.Known(true)})
			case rectContains(walls, c):
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(false), Things: OccupantThings(true), Indoors: domain.Known(false), Roofed: domain.Known(true)})
			default:
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Things: OccupantThings(false), Indoors: domain.Known(false), Roofed: domain.Known(false)})
			}
		}
	}
	v := domain.Known(RoomObservation{Shapes: testShapes, EligibleBeds: domain.Known(room.Beds), Rooms: []Room{room}})

	powered, err := SelectTemperatureMethod(v, coolingReady(cells, 500), DefaultRoundsPolicy(), RoundsLatches{Hot: true})
	if err != nil || powered.Method != TemperatureCoolPowered || powered.Room != "shelter" {
		t.Fatalf("powered: %+v %v", powered, err)
	}
	if !rectContains(walls, powered.Cell) || rectContains(in, powered.Cell) || powered.Cell == shelter.Door {
		t.Fatalf("the cooler stands at %v, not on a wall of the shelter %v (door %v)", powered.Cell, in, shelter.Door)
	}
	passive, err := SelectTemperatureMethod(v, TemperatureCooling{}, DefaultRoundsPolicy(), RoundsLatches{Hot: true})
	if err != nil || passive.Method != TemperatureCool || len(passive.Cells) != int(in.Width*in.Height) {
		t.Fatalf("passive: %+v %v", passive, err)
	}
}

// The latch comes from the survey through the real derive path.
func TestDeriveLatchesTheSurveyClimate(t *testing.T) {
	slowtest.Skip(t, "two full layout derives on a 120-cell map; runs under cmd/test -full and nightly")
	t.Parallel()
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(120, open)
	for _, cold := range []bool{false, true} {
		s.Cold = cold
		plan, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 0, 0).Value()
		if !ok || plan.Cold != cold {
			t.Fatalf("survey cold=%v: plan ok=%v cold=%v", cold, ok, plan.Cold)
		}
		got, want := shelterOf(t, plan).Interior, ShelterSizes(3, ShelterCampfires(cold), 0)[0]
		if got.Width != want[0] || got.Height != want[1] {
			t.Errorf("survey cold=%v: shelter %dx%d, want %v", cold, got.Width, got.Height, want)
		}
	}
}
