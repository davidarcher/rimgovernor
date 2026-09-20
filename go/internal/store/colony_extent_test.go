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
	report, err := db.ReconcileColonyExtent(ctx, world, 200)
	if err != nil || report.Visible != 2 || report.Discarded != 0 || report.Parent != "" {
		t.Fatal(report, err)
	}
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

func TestColonyExtentRewindRestoresOnlyThatGenerationsHistory(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := caravanTrackingFixture(t)
	first := extentWorld("colony", "load-1", 1)
	for i, tick := range []domain.Tick{100, 200, 300} {
		if tick == 300 {
			if err := db.AddExpansionArea(ctx, first, 250, "late", []domain.Cell{{X: 9, Z: 9}}, "added after the save"); err != nil {
				t.Fatal(err)
			}
		}
		if _, err := db.EstablishColonyExtent(ctx, first, tick, []policy.ExtentRegion{extentRegion(string(rune('a'+i)), domain.Cell{X: int32(i), Z: 0})}); err != nil {
			t.Fatal(err)
		}
	}
	// Loading the save taken at tick 200 forks from load-1 there.
	rewound := extentWorld("colony", "load-2", 1)
	report, err := db.ReconcileColonyExtent(ctx, rewound, 200)
	if err != nil || report.Parent != "load-1" || report.Visible != 2 || report.Beyond != 2 {
		t.Fatal(report, err)
	}
	if got := extentRegions(t, db, rewound, 200); !reflect.DeepEqual(got, []string{"a", "b"}) {
		t.Fatal(got)
	}
	if areas, err := db.ExpansionAreas(ctx, rewound, 200); err != nil || len(areas) != 0 {
		t.Fatal(areas, err)
	}
	// Playing past the fork on the new branch never reveals the old branch.
	if _, err = db.EstablishColonyExtent(ctx, rewound, 260, []policy.ExtentRegion{extentRegion("d", domain.Cell{X: 5, Z: 5})}); err != nil {
		t.Fatal(err)
	}
	if got := extentRegions(t, db, rewound, 400); !reflect.DeepEqual(got, []string{"a", "b", "d"}) {
		t.Fatal(got)
	}
	// A later save of the first branch restores that branch alone.
	third := extentWorld("colony", "load-3", 1)
	if report, err = db.ReconcileColonyExtent(ctx, third, 300); err != nil || report.Parent != "load-1" {
		t.Fatal(report, err)
	}
	if got := extentRegions(t, db, third, 300); !reflect.DeepEqual(got, []string{"a", "b", "c"}) {
		t.Fatal(got)
	}
	// A save inside the span both branches cover comes from the branch
	// played most recently, and sees the lineage beneath its fork.
	fourth := extentWorld("colony", "load-4", 1)
	if report, err = db.ReconcileColonyExtent(ctx, fourth, 220); err != nil || report.Parent != "load-2" {
		t.Fatal(report, err)
	}
	if got := extentRegions(t, db, fourth, 500); !reflect.DeepEqual(got, []string{"a", "b"}) {
		t.Fatal(got)
	}
	// A tick rewind inside one load discards that load's later entries.
	if report, err = db.ReconcileColonyExtent(ctx, rewound, 210); err != nil || report.Discarded != 1 {
		t.Fatal(report, err)
	}
	if got := extentRegions(t, db, rewound, 400); !reflect.DeepEqual(got, []string{"a", "b"}) {
		t.Fatal(got)
	}
}

func TestColonyExtentIsolatesWorlds(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := caravanTrackingFixture(t)
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
		report, err := db.ReconcileColonyExtent(ctx, s, 500)
		if err != nil || report.Parent != "" || report.Visible != 0 {
			t.Fatal(report, err)
		}
		if rows, err := db.EstablishedColonyExtent(ctx, s, 500); err != nil || len(rows) != 0 {
			t.Fatal(rows, err)
		}
		if areas, err := db.ExpansionAreas(ctx, s, 500); err != nil || len(areas) != 0 {
			t.Fatal(areas, err)
		}
	}
	// A reload of the original colony at a later tick continues its history.
	reloaded := extentWorld("colony", "load-2", 2)
	if got := extentRegions(t, db, reloaded, 900); len(got) != 0 {
		t.Fatal("unreconciled load saw history", got)
	}
	if report, err := db.ReconcileColonyExtent(ctx, reloaded, 900); err != nil || report.Parent != "load-1" || report.Visible != 2 {
		t.Fatal(report, err)
	}
	if got := extentRegions(t, db, reloaded, 900); !reflect.DeepEqual(got, []string{"a"}) {
		t.Fatal(got)
	}
}

func TestExpansionAreaLifecycle(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := caravanTrackingFixture(t)
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
