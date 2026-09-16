package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func cleanlinessRooms(kitchenC, barnC float64) domain.Fact[RoomObservation] {
	return domain.Known(RoomObservation{Rooms: []Room{
		{ID: "k", Cells: []domain.Cell{{X: 5, Z: 5}, {X: 4, Z: 6}}, Role: domain.Known(RoomRoleRoom), Enclosed: domain.Known(true), Cleanliness: domain.Known(kitchenC), Contents: domain.Known([]Amount{{Resource: "FueledStove", Count: 1}})},
		{ID: "b", Role: domain.Known(RoomRoleBarn), Enclosed: domain.Known(true), Cleanliness: domain.Known(barnC)},
		{ID: "s", Role: domain.Known(RoomRoleKitchen), Enclosed: domain.Known(true), Cleanliness: domain.Known(-9.0), Contents: domain.Known([]Amount{{Resource: "ButcherSpot", Count: 1}})},
	}})
}

func cleanlinessFilth() domain.Fact[[]UpkeepFilth] {
	return domain.Known([]UpkeepFilth{
		{ID: "k2", Home: true, RoomID: domain.Known("k"), Thickness: 2},
		{ID: "k1", Home: true, RoomID: domain.Known("k"), Thickness: 1},
		{ID: "b1", Home: true, RoomID: domain.Known("b"), Thickness: 5},
		{ID: "s1", Home: true, RoomID: domain.Known("s"), Thickness: 5},
		{ID: "o1", Home: true, Thickness: 5},
		{ID: "k3", Home: false, RoomID: domain.Known("k"), Thickness: 5},
	})
}

func targetIDs(t *testing.T, r CleanlinessReview) []string {
	t.Helper()
	rows, known := r.Targets.Value()
	if !known {
		t.Fatalf("targets unknown")
	}
	ids := []string{}
	for _, row := range rows {
		ids = append(ids, row.ID)
	}
	return ids
}

func TestCleanlinessCoverageGraceThenTargets(t *testing.T) {
	p := DefaultCleanlinessPolicy()
	// Dirty kitchen with a cleaner: latch now, no targets until the grace
	// period elapses. Barn and butcher room are never targets.
	r, err := ReviewCleanliness(cleanlinessRooms(-3, -8), cleanlinessFilth(), domain.Known(1), nil, 100, p)
	if err != nil || !reflect.DeepEqual(r.DirtyRooms, []DirtyRoom{{Key: "4,6", Since: 100}}) || len(targetIDs(t, r)) != 0 {
		t.Fatalf("grace: %+v %v", r, err)
	}
	r, err = ReviewCleanliness(cleanlinessRooms(-3, -8), cleanlinessFilth(), domain.Known(1), r.DirtyRooms, 100+p.GraceTicks-1, p)
	if err != nil || len(targetIDs(t, r)) != 0 {
		t.Fatalf("still within grace: %+v %v", r, err)
	}
	r, err = ReviewCleanliness(cleanlinessRooms(-3, -8), cleanlinessFilth(), domain.Unknown[int](), r.DirtyRooms, 100+p.GraceTicks, p)
	metric, _ := r.Metric.Value()
	if err != nil || !reflect.DeepEqual(targetIDs(t, r), []string{"k1", "k2"}) || metric != 3 || !reflect.DeepEqual(r.DirtyRooms, []DirtyRoom{{Key: "4,6", Since: 100}}) {
		t.Fatalf("after grace: %+v %v", r, err)
	}
}

func TestCleanlinessNoCleanersIsImmediate(t *testing.T) {
	r, err := ReviewCleanliness(cleanlinessRooms(-3, -8), cleanlinessFilth(), domain.Known(0), nil, 0, DefaultCleanlinessPolicy())
	if err != nil || !reflect.DeepEqual(targetIDs(t, r), []string{"k1", "k2"}) {
		t.Fatalf("no cleaners: %+v %v", r, err)
	}
}

func TestCleanlinessHysteresisAndRelease(t *testing.T) {
	p := DefaultCleanlinessPolicy()
	latched := []DirtyRoom{{Key: "4,6", Since: 5}}
	// Between exit and enter thresholds: held while latched, never entered.
	r, err := ReviewCleanliness(cleanlinessRooms(-0.5, 0), cleanlinessFilth(), domain.Known(0), latched, 10, p)
	if err != nil || !reflect.DeepEqual(r.DirtyRooms, latched) || !reflect.DeepEqual(targetIDs(t, r), []string{"k1", "k2"}) {
		t.Fatalf("hold: %+v %v", r, err)
	}
	r, err = ReviewCleanliness(cleanlinessRooms(-0.5, 0), cleanlinessFilth(), domain.Known(0), nil, 10, p)
	if err != nil || len(r.DirtyRooms) != 0 || len(targetIDs(t, r)) != 0 {
		t.Fatalf("no enter: %+v %v", r, err)
	}
	r, err = ReviewCleanliness(cleanlinessRooms(0, 0), cleanlinessFilth(), domain.Known(0), latched, 10, p)
	if err != nil || len(r.DirtyRooms) != 0 || len(targetIDs(t, r)) != 0 {
		t.Fatalf("release: %+v %v", r, err)
	}
	// Unknown stat keeps the latch but cannot target; unknown census keeps
	// the latch and yields unknown targets; empty filth is a known clean set.
	rooms := cleanlinessRooms(0, 0)
	census, _ := rooms.Value()
	census.Rooms[0].Cleanliness = domain.Unknown[float64]()
	r, err = ReviewCleanliness(domain.Known(census), cleanlinessFilth(), domain.Known(0), latched, 10, p)
	if err != nil || !reflect.DeepEqual(r.DirtyRooms, latched) || len(targetIDs(t, r)) != 0 {
		t.Fatalf("unknown stat: %+v %v", r, err)
	}
	r, err = ReviewCleanliness(domain.Unknown[RoomObservation](), cleanlinessFilth(), domain.Known(0), latched, 10, p)
	if _, known := r.Targets.Value(); err != nil || !reflect.DeepEqual(r.DirtyRooms, latched) || known {
		t.Fatalf("unknown census: %+v %v", r, err)
	}
	r, err = ReviewCleanliness(domain.Unknown[RoomObservation](), domain.Known([]UpkeepFilth{}), domain.Known(0), latched, 10, p)
	if err != nil || len(targetIDs(t, r)) != 0 {
		t.Fatalf("empty filth: %+v %v", r, err)
	}
}

func TestCleanlinessOrderAndBound(t *testing.T) {
	p := DefaultCleanlinessPolicy()
	p.MaxTargets = 2
	rooms := domain.Known(RoomObservation{Rooms: []Room{
		{ID: "h", Role: domain.Known(RoomRoleHospital), Enclosed: domain.Known(true), Cleanliness: domain.Known(-2.0)},
		{ID: "k", Role: domain.Known(RoomRoleKitchen), Enclosed: domain.Known(true), Cleanliness: domain.Known(-6.0)},
	}})
	filth := domain.Known([]UpkeepFilth{
		{ID: "h1", Home: true, RoomID: domain.Known("h"), Thickness: 1},
		{ID: "k2", Home: true, RoomID: domain.Known("k"), Thickness: 1},
		{ID: "k1", Home: true, RoomID: domain.Known("k"), Thickness: 1},
	})
	r, err := ReviewCleanliness(rooms, filth, domain.Known(0), nil, 0, p)
	if err != nil || !reflect.DeepEqual(targetIDs(t, r), []string{"k1", "k2"}) {
		t.Fatalf("order: %+v %v", r, err)
	}
	if _, err := ReviewCleanliness(rooms, filth, domain.Known(0), []DirtyRoom{{Key: "k", Since: 5}}, 0, p); err == nil {
		t.Fatalf("future latch accepted")
	}
	if _, err := ReviewCleanliness(rooms, filth, domain.Known(0), nil, 0, CleanlinessPolicy{EnterC: 0, ExitC: -1, GraceTicks: 1, MaxTargets: 1}); err == nil {
		t.Fatalf("inverted thresholds accepted")
	}
}

func TestKitchenSeparation(t *testing.T) {
	rooms := domain.Known(RoomObservation{Rooms: []Room{
		{ID: "k", Cells: []domain.Cell{{X: 1, Z: 1}}, Contents: domain.Known([]Amount{{Resource: "ElectricStove", Count: 1}, {Resource: "ButcherSpot", Count: 1}})},
		{ID: "c", Cells: []domain.Cell{{X: 2, Z: 2}}, Contents: domain.Known([]Amount{{Resource: "Campfire", Count: 1}})},
		{ID: "b", Cells: []domain.Cell{{X: 3, Z: 3}}, Contents: domain.Known([]Amount{{Resource: "TableButcher", Count: 1}})},
	}})
	got, known := KitchenSeparation(rooms).Value()
	if !known || !reflect.DeepEqual(got, []SeparationRoom{{ID: "k", Cells: []domain.Cell{{X: 1, Z: 1}}}}) {
		t.Fatalf("separation: %+v", got)
	}
	if cells := SeparationProtectedCells(rooms, true); !reflect.DeepEqual(cells, []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 2}}) {
		t.Fatalf("butcher placement protects cooking rooms: %+v", cells)
	}
	if cells := SeparationProtectedCells(rooms, false); !reflect.DeepEqual(cells, []domain.Cell{{X: 1, Z: 1}, {X: 3, Z: 3}}) {
		t.Fatalf("cooking placement protects butcher rooms: %+v", cells)
	}
	if _, known := KitchenSeparation(domain.Unknown[RoomObservation]()).Value(); known || SeparationProtectedCells(domain.Unknown[RoomObservation](), true) != nil {
		t.Fatalf("unknown census")
	}
}
