package movement

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func movementCells() map[string]any {
	var rows []any
	for _, x := range []float64{0, 2, 3, 6} {
		rows = append(rows, map[string]any{"cell": map[string]any{"x": x, "z": 0.0}, "terrain": "Soil", "walkable": true, "passable": true, "fogged": false})
	}
	return map[string]any{"cells": rows, "completeness": map[string]any{}}
}

func TestCandidatesBoundedAndObservedTraversalRequired(t *testing.T) {
	origin := map[string]any{"x": 0.0, "z": 0.0}
	got, err := candidates(movementCells(), origin)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := []map[string]any{{"x": 2.0, "z": 0.0}, {"x": 3.0, "z": 0.0}}
	if !na.DeepEqual(got, want) {
		t.Fatalf("unexpected candidates: %#v", got)
	}

	type mutation struct {
		field string
		value any
	}
	for _, m := range []mutation{{"walkable", false}, {"passable", false}, {"fogged", true}, {"walkable", 1.0}} {
		value := movementCells()
		rows := na.AsSlice(value["cells"])
		row, _ := na.AsMap(rows[1]) // the x=2 cell
		row[m.field] = m.value
		got, err := candidates(value, origin)
		if err != nil {
			t.Fatalf("unexpected error for mutation %+v: %v", m, err)
		}
		want := []map[string]any{{"x": 3.0, "z": 0.0}}
		if !na.DeepEqual(got, want) {
			t.Fatalf("mutation %+v: expected only the x=3 cell, got %#v", m, got)
		}
	}
}
