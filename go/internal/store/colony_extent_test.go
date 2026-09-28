package store

import (
	"context"
	"errors"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func extentWorld(colony, load string, native domain.NativeGeneration) domain.GenerationSnapshot {
	return domain.GenerationSnapshot{Colony: domain.ColonyID(colony), Map: 1, Load: domain.LoadID(load), Plan: "root", Revision: 1, Native: native}
}

func extentRegion(facility string, cells ...domain.Cell) policy.ExtentRegion {
	region := policy.ExtentRegion{}
	for _, c := range cells {
		region.Cells = append(region.Cells, policy.ExtentCell{Cell: c, Provenance: []policy.ExtentProvenance{{Origin: policy.ExtentFacility, Facility: facility, Plan: "plan-1", Action: "act-1", Goal: "goal-1"}}})
	}
	return region
}

func extentRegions(t *testing.T, db *Store, s domain.GenerationSnapshot, tick domain.Tick) []string {
	t.Helper()
	rows, err := db.EstablishedColonyExtent(context.Background(), s, tick)
	if err != nil {
		t.Fatal(err)
	}
	var out []string
	for _, row := range rows {
		out = append(out, row.Region.Cells[0].Provenance[0].Facility)
	}
	return out
}

func TestExtentEligibilityDoesNotRewritePersistedHistory(t *testing.T) {
	ctx := context.Background()
	db, err := Open(ctx, filepath.Join(t.TempDir(), "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	w := extentWorld("colony", "load", 1)
	region := extentRegion("bed", domain.Cell{X: 2, Z: 3})
	if _, err := db.EstablishColonyExtent(ctx, w, 100, []policy.ExtentRegion{region}); err != nil {
		t.Fatal(err)
	}
	before, err := db.EstablishedColonyExtent(ctx, w, 100)
	if err != nil {
		t.Fatal(err)
	}
	r := policy.ExtentEligibilityRequest{Extent: domain.Known(policy.ColonyExtent{Regions: []policy.ExtentRegion{before[0].Region}}), Facilities: domain.Known([]string{"bed"}), Threat: domain.Known(false), Regions: map[int]policy.ExtentRegionObservation{0: {RouteObservedPassable: domain.Known(true)}}}
	if !policy.ExtentEligibility(r).Regions[0].Eligible {
		t.Fatal("initial region held")
	}
	r.Threat = domain.Known(true)
	if got := policy.ExtentEligibility(r).Regions[0]; got.Eligible || !reflect.DeepEqual(got.HoldReasons, []string{"threat_present"}) {
		t.Fatal(got)
	}
	r.Threat = domain.Known(false)
	r.Facilities = domain.Known([]string{})
	if got := policy.ExtentEligibility(r).Regions[0]; got.Eligible || len(got.ActiveFacilities) != 0 || !reflect.DeepEqual(got.HoldReasons, []string{"facility_lost"}) {
		t.Fatal(got)
	}
	after, err := db.EstablishedColonyExtent(ctx, w, 200)
	if err != nil || !reflect.DeepEqual(before, after) {
		t.Fatalf("history changed: %v %v", after, err)
	}
}

func TestColonyExtentPersistsAcrossReopenWithProvenance(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "state.sqlite")
	db, err := Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	world := extentWorld("colony", "load-1", 7)
	region := extentRegion("bench", domain.Cell{X: 3, Z: 4}, domain.Cell{X: 3, Z: 5})
	if n, err := db.EstablishColonyExtent(ctx, world, 100, []policy.ExtentRegion{region}); err != nil || n != 1 {
		t.Fatal(n, err)
	}
	// The same region observed again is history already, not a new entry.
	if n, err := db.EstablishColonyExtent(ctx, world, 150, []policy.ExtentRegion{region}); err != nil || n != 0 {
		t.Fatal(n, err)
	}
	if err = db.AddExpansionArea(ctx, world, 120, "east-field", []domain.Cell{{X: 10, Z: 1}, {X: 10, Z: 2}}, "player marked farmland"); err != nil {
		t.Fatal(err)
	}
	db.Close()
	db, err = Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	// The next process observes the same load at a later tick.
	rows, err := db.EstablishedColonyExtent(ctx, world, 200)
	if err != nil || len(rows) != 1 {
		t.Fatal(rows, err)
	}
	want := EstablishedExtent{Snapshot: world, Tick: 100, Region: region}
	if !reflect.DeepEqual(rows[0], want) {
		t.Fatalf("got %+v want %+v", rows[0], want)
	}
	areas, err := db.ExpansionAreas(ctx, world, 200)
	if err != nil || len(areas) != 1 || areas[0].ID != "east-field" || areas[0].Reason != "player marked farmland" || areas[0].Tick != 120 || areas[0].Snapshot != world {
		t.Fatal(areas, err)
	}
	if !reflect.DeepEqual(areas[0].Cells, []domain.Cell{{X: 10, Z: 1}, {X: 10, Z: 2}}) {
		t.Fatal(areas[0].Cells)
	}
}

// A new load starts empty and re-establishes from the live world (#1009);
// ticks past the read are not visible.
func TestColonyExtentIsEmptiedOnAWorldChange(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := open(t, memoryPath(t))
	first := extentWorld("colony", "load-1", 1)
	for i, tick := range []domain.Tick{100, 200} {
		if _, err := db.EstablishColonyExtent(ctx, first, tick, []policy.ExtentRegion{extentRegion(string(rune('a'+i)), domain.Cell{X: int32(i), Z: 0})}); err != nil {
			t.Fatal(err)
		}
	}
	if got := extentRegions(t, db, first, 150); !reflect.DeepEqual(got, []string{"a"}) {
		t.Fatal(got)
	}
	if err := db.RebuildFamilies(ctx, nil); err != nil {
		t.Fatal(err)
	}
	if got := extentRegions(t, db, extentWorld("colony", "load-2", 1), 300); len(got) != 0 {
		t.Fatal("extent survived a world change", got)
	}
}

func TestColonyExtentIsolatesWorlds(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := open(t, memoryPath(t))
	world := extentWorld("colony", "load-1", 1)
	if _, err := db.EstablishColonyExtent(ctx, world, 100, []policy.ExtentRegion{extentRegion("a", domain.Cell{X: 0, Z: 0})}); err != nil {
		t.Fatal(err)
	}
	if err := db.AddExpansionArea(ctx, world, 100, "area", []domain.Cell{{X: 1, Z: 1}}, "reason"); err != nil {
		t.Fatal(err)
	}
	other := extentWorld("other-colony", "load-9", 1)
	otherMap := world
	otherMap.Map = 2
	for _, s := range []domain.GenerationSnapshot{other, otherMap} {
		if rows, err := db.EstablishedColonyExtent(ctx, s, 500); err != nil || len(rows) != 0 {
			t.Fatal(rows, err)
		}
		if areas, err := db.ExpansionAreas(ctx, s, 500); err != nil || len(areas) != 0 {
			t.Fatal(areas, err)
		}
	}
}

func TestExpansionAreaLifecycle(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := open(t, memoryPath(t))
	world := extentWorld("colony", "load-1", 1)
	cells := []domain.Cell{{X: 1, Z: 1}}
	if err := db.RemoveExpansionArea(ctx, world, 10, "area", "nothing to remove"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	if err := db.AddExpansionArea(ctx, world, 10, "area", cells, "first"); err != nil {
		t.Fatal(err)
	}
	if err := db.AddExpansionArea(ctx, world, 11, "area", cells, "replay"); err != nil {
		t.Fatal(err)
	}
	if err := db.AddExpansionArea(ctx, world, 12, "area", []domain.Cell{{X: 2, Z: 2}}, "moved"); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if err := db.RemoveExpansionArea(ctx, world, 20, "area", "player cleared it"); err != nil {
		t.Fatal(err)
	}
	if areas, err := db.ExpansionAreas(ctx, world, 30); err != nil || len(areas) != 0 {
		t.Fatal(areas, err)
	}
	// The removal is a journal entry: the area is still live before it.
	if areas, err := db.ExpansionAreas(ctx, world, 15); err != nil || len(areas) != 1 || areas[0].Reason != "first" {
		t.Fatal(areas, err)
	}
	if err := db.AddExpansionArea(ctx, world, 40, "area", []domain.Cell{{X: 2, Z: 2}}, "re-added"); err != nil {
		t.Fatal(err)
	}
	for _, bad := range []struct {
		id, reason string
		cells      []domain.Cell
	}{
		{"", "reason", cells}, {"area", " ", cells}, {"area", "reason", nil},
		{"area", "reason", []domain.Cell{{X: 2, Z: 2}, {X: 1, Z: 1}}}, {"area", "reason", []domain.Cell{{X: -1, Z: 0}}},
	} {
		if err := db.AddExpansionArea(ctx, world, 50, bad.id, bad.cells, bad.reason); err == nil {
			t.Fatal("accepted", bad)
		}
	}
	if _, err := db.EstablishColonyExtent(ctx, world, 50, []policy.ExtentRegion{{Cells: []policy.ExtentCell{{Cell: domain.Cell{X: 1, Z: 1}}}}}); err == nil {
		t.Fatal("accepted a region without provenance")
	}
}
