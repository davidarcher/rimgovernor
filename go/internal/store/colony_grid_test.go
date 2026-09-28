package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func testGrid(x, z int32) policy.ColonyGrid {
	return policy.ColonyGrid{Origin: domain.Cell{X: x, Z: z}, Pitch: policy.GridPitch, Axes: policy.ColonyGridAxes, Source: policy.ColonyGridFromStarter}
}

func TestColonyGridPersistsAcrossReopenAndNeverMoves(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "state.sqlite")
	db, err := Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	w := extentWorld("colony", "load", 3)
	if _, ok, err := db.ColonyGrid(ctx, w, 50); err != nil || ok {
		t.Fatalf("fresh world holds a grid: %v %v", ok, err)
	}
	record, established, err := db.EstablishColonyGrid(ctx, w, 100, testGrid(40, 50))
	if err != nil || !established || record.Grid != testGrid(40, 50) || record.Tick != 100 {
		t.Fatal(record, established, err)
	}
	// A second derivation with different evidence never moves the grid.
	moved := testGrid(7, 7)
	moved.Source = policy.ColonyGridFromRoom
	record, established, err = db.EstablishColonyGrid(ctx, w, 200, moved)
	if err != nil || established || record.Grid != testGrid(40, 50) {
		t.Fatal(record, established, err)
	}
	db.Close()
	db, err = Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	record, ok, err := db.ColonyGrid(ctx, w, 300)
	if err != nil || !ok || record.Grid != testGrid(40, 50) || record.Tick != 100 || record.Snapshot.Native != 3 {
		t.Fatal(record, ok, err)
	}
	if _, ok, err := db.ColonyGrid(ctx, extentWorld("other", "load", 3), 300); err != nil || ok {
		t.Fatal("another colony sees the grid", ok, err)
	}
	if _, _, err := db.EstablishColonyGrid(ctx, w, 100, policy.ColonyGrid{}); err == nil {
		t.Fatal("an invalid grid was accepted")
	}
}

func TestColonyGridIsEmptiedOnAWorldChange(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := open(t, memoryPath(t))
	first := extentWorld("colony", "load-1", 1)
	if _, _, err := db.EstablishColonyGrid(ctx, first, 250, testGrid(40, 50)); err != nil {
		t.Fatal(err)
	}
	if _, ok, err := db.ColonyGrid(ctx, first, 240); err != nil || ok {
		t.Fatal("grid visible before its tick", ok, err)
	}
	// A world change empties the session cache; the new session fixes its
	// own grid from the live world (#1009, #976 U4b).
	if err := db.RebuildFamilies(ctx, nil); err != nil {
		t.Fatal(err)
	}
	next := extentWorld("colony", "load-2", 1)
	if _, ok, err := db.ColonyGrid(ctx, next, 400); err != nil || ok {
		t.Fatal("grid survived a world change", ok, err)
	}
	if _, established, err := db.EstablishColonyGrid(ctx, next, 220, testGrid(8, 8)); err != nil || !established {
		t.Fatal(established, err)
	}
}
