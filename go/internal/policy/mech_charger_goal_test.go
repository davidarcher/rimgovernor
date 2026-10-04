package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestEnsureMechChargerRaisesGoalOnlyWhereTheNeedIsKnown(t *testing.T) {
	t.Parallel()
	f := stableRoutine()
	f.MechChargerOwed = domain.Unknown[bool]()
	r := needs(t, f, RoutineLatches{})
	if hasNeed(r, EnsureMechCharger) || assessment(t, r, EnsureMechCharger) != domain.FindingUnclear {
		t.Fatal("no charger fact: an unknown assessment and no goal", r)
	}
	f.AvailableMethods = domain.Known([]ConcernID{EnsureMechCharger})
	f.MechChargerOwed = domain.Known(true)
	r = needs(t, f, r.Latches)
	if !hasNeed(r, EnsureMechCharger) {
		t.Fatal("an owed charger opens the goal", r)
	}
	if got, _ := RoutineDevelopmentDeficit(EnsureMechCharger, f, RoutinePolicy{}).Value(); got != 1 {
		t.Fatal("owed is a full deficit", got)
	}
	f.MechChargerOwed = domain.Known(false)
	r = needs(t, f, r.Latches)
	if hasNeed(r, EnsureMechCharger) || assessment(t, r, EnsureMechCharger) != domain.FindingMet {
		t.Fatal("no charger owed settles the goal", r)
	}
	if got, ok := RoutineDevelopmentDeficit(EnsureMechCharger, f, RoutinePolicy{}).Value(); !ok || got != 0 {
		t.Fatal("none owed is no deficit", got, ok)
	}
	f.MechChargerOwed = domain.Unknown[bool]()
	if _, ok := RoutineDevelopmentDeficit(EnsureMechCharger, f, RoutinePolicy{}).Value(); ok {
		t.Fatal("an unread need is an unknown deficit")
	}
}

func TestMechChargerNeedIsUnknownUntilMechsAndChargersAreRead(t *testing.T) {
	t.Parallel()
	fleet := domain.Known(MechFleet{Mechanitors: []MechanitorInput{{ID: "M"}}})
	idle := domain.Known([]MechCharger{{Charging: domain.Known(false), FullOfWaste: domain.Known(false)}})
	for name, c := range map[string]struct {
		fleet    domain.Fact[MechFleet]
		chargers domain.Fact[[]MechCharger]
		known    bool
		owed     bool
	}{
		"no mechs read":    {domain.Unknown[MechFleet](), idle, false, false},
		"no chargers read": {fleet, domain.Unknown[[]MechCharger](), false, false},
		"none standing":    {fleet, domain.Known([]MechCharger{}), true, true},
		"all busy":         {fleet, domain.Known([]MechCharger{{Charging: domain.Known(true), FullOfWaste: domain.Known(false)}}), true, true},
		"one idle":         {fleet, idle, true, false},
		"no mechanitor":    {domain.Known(MechFleet{}), domain.Known([]MechCharger{}), true, false},
	} {
		owed, known := MechChargerNeed(c.fleet, c.chargers).Value()
		if known != c.known || owed != c.owed {
			t.Errorf("%s: owed %v known %v", name, owed, known)
		}
	}
}

func freeCells(w, h int32) []SiteCell {
	var out []SiteCell
	for x := int32(0); x < w; x++ {
		for z := int32(0); z < h; z++ {
			out = append(out, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: yes(true), Occupied: yes(false), Zone: yes(false), Doorway: yes(false)})
		}
	}
	return out
}

func TestMechChargerSitesRankByPollutionSitingOverFreeGround(t *testing.T) {
	t.Parallel()
	cells := freeCells(10, 4)
	for i := range cells {
		c := &cells[i]
		switch {
		case c.Cell.X == 0: // a growing field along the west edge
			c.Zone, c.ZoneID = yes(true), domain.Known("field")
		case c.Cell.X == 5 && c.Cell.Z == 0: // a cell another building holds
			c.Occupied = yes(true)
		case c.Cell.X == 9: // a stockpile that is not a field is no free ground
			c.Zone, c.ZoneID = yes(true), domain.Known("stock")
		}
	}
	rooms := domain.Known(RoomObservation{Shapes: testShapes, Rooms: []Room{
		{ID: "bed", Role: domain.Known(RoomRoleBedroom), Cells: []domain.Cell{{X: 1, Z: 3}}},
		{ID: "unread", Cells: []domain.Cell{{X: 8, Z: 0}}},
	}})
	got, err := MechChargerSites(MechChargerSiteFacts{
		Bounds: Bounds{10, 4}, Cells: cells, FieldZones: map[string]bool{"field": true}, Rooms: rooms, Disposal: []domain.Cell{{X: 6, Z: 3}},
	}, 2, 2)
	if err != nil || len(got) == 0 {
		t.Fatal(got, err)
	}
	for _, s := range got {
		for _, c := range rectCells(s.Site) {
			if c.X == 0 || c.X == 9 || c == (domain.Cell{X: 5, Z: 0}) {
				t.Fatalf("site %+v covers a zone or an occupied cell", s.Site)
			}
		}
	}
	// The far east side keeps clear of both the field (x=0) and the bedroom.
	if want := (Rectangle{X: 7, Z: 0, Width: 2, Height: 2}); got[0].Site != want && got[0].Site != (Rectangle{X: 7, Z: 1, Width: 2, Height: 2}) {
		t.Fatalf("best site %+v, want the east edge (%+v)", got[0].Site, want)
	}
	again, _ := MechChargerSites(MechChargerSiteFacts{Bounds: Bounds{10, 4}, Cells: cells, FieldZones: map[string]bool{"field": true}, Rooms: rooms, Disposal: []domain.Cell{{X: 6, Z: 3}}}, 2, 2)
	for i := range got {
		if got[i] != again[i] {
			t.Fatal("ranking is deterministic")
		}
	}
}

func TestMechChargerSitesNeverGuessFreeGround(t *testing.T) {
	t.Parallel()
	cells := freeCells(3, 3)
	cells[4].Occupied = domain.Unknown[bool]()
	got, err := MechChargerSites(MechChargerSiteFacts{Bounds: Bounds{3, 3}, Cells: cells}, 2, 2)
	if err != nil || len(got) != 0 {
		t.Fatal("an unread cell blocks every footprint over it", got, err)
	}
	if got, err = MechChargerSites(MechChargerSiteFacts{Bounds: Bounds{3, 3}, Cells: freeCells(3, 3)}, 4, 1); err != nil || len(got) != 0 {
		t.Fatal("a footprint wider than the map has no site", got, err)
	}
}
