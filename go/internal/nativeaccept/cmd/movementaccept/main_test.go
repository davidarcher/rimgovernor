package main

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func movementCells() map[string]any {
	var rows []any
	for _, x := range []float64{0, 2, 3, 6} {
		rows = append(rows, map[string]any{"cell": map[string]any{"x": x, "z": 0.0}, "terrain": "Soil", "walkable": true, "passable": true, "fogged": false})
	}
	return map[string]any{"cells": rows, "completeness": map[string]any{
		"page": map[string]any{"complete": true}, "matched": "4", "returned": "4", "unreadable": "0",
	}}
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

func TestCandidatesPartialCellsCannotProveSafeDestination(t *testing.T) {
	value := movementCells()
	completeness, _ := na.AsMap(value["completeness"])
	completeness["unreadable"] = "1"
	if _, err := candidates(value, map[string]any{"x": 0.0, "z": 0.0}); err == nil {
		t.Fatal("expected an error for a page with unreadable cells")
	}
}

// movementFixture is the fixture: a completed
// Goto job that arrived at destination.
func movementFixture() (destination, effect, row, progress map[string]any) {
	destination = map[string]any{"x": 2.0, "z": 0.0}
	effect = map[string]any{
		"pawnId": "Human1", "jobId": 58.0, "jobDef": "Goto", "issued": true, "verified": true,
		"targetA": map[string]any{"cell": destination}, "draftClaimId": "claim",
	}
	row = map[string]any{
		"pawn": map[string]any{"id": "Human1", "position": destination, "snapshot": map[string]any{"token": "native"}},
		"job":  map[string]any{"loadId": "58", "defName": "Goto"}, "draftClaim": map[string]any{"owned": map[string]any{"claimId": "claim"}},
	}
	progress = map[string]any{"completeInspection": true, "completed": map[string]any{"evidence": map[string]any{"job": copyAny(effect)}}}
	return
}

func copyAny(m map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range m {
		if inner, ok := v.(map[string]any); ok {
			out[k] = copyAny(inner)
			continue
		}
		out[k] = v
	}
	return out
}

func TestJobEffectRequiresExactLiveJobIdentity(t *testing.T) {
	destination, effect, row, _ := movementFixture()
	if _, err := jobEffect(map[string]any{"applied": map[string]any{"observed": map[string]any{"job": effect}}}, row, destination); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	job, _ := na.AsMap(row["job"])
	job["loadId"] = "59"
	if _, err := jobEffect(map[string]any{"applied": map[string]any{"observed": map[string]any{"job": effect}}}, row, destination); err == nil {
		t.Fatal("expected an error once the row's job no longer matches the issued effect")
	}
}

func TestArrivalRequiresPositionAndCausalCompletedEvidence(t *testing.T) {
	for _, bad := range []string{"position", "pending", "inspection", "job", "claim", "target"} {
		t.Run(bad, func(t *testing.T) {
			destination, effect, row, progress := movementFixture()
			if err := arrival(progress, row, destination, effect); err != nil {
				t.Fatalf("fixture must pass before mutation: %v", err)
			}
			switch bad {
			case "position":
				pawn, _ := na.AsMap(row["pawn"])
				pawn["position"] = map[string]any{"x": 1.0, "z": 0.0}
			case "pending":
				progress["pending"] = progress["completed"]
				delete(progress, "completed")
			case "inspection":
				progress["completeInspection"] = false
			case "job":
				completed, _ := na.AsMap(progress["completed"])
				evidence, _ := na.AsMap(completed["evidence"])
				job, _ := na.AsMap(evidence["job"])
				job["jobId"] = 59.0
			case "claim":
				draftClaim, _ := na.AsMap(row["draftClaim"])
				owned, _ := na.AsMap(draftClaim["owned"])
				owned["claimId"] = "replacement"
			default:
				completed, _ := na.AsMap(progress["completed"])
				evidence, _ := na.AsMap(completed["evidence"])
				job, _ := na.AsMap(evidence["job"])
				targetA, _ := na.AsMap(job["targetA"])
				targetA["cell"] = map[string]any{"x": 3.0, "z": 0.0}
			}
			if err := arrival(progress, row, destination, effect); err == nil {
				t.Fatalf("expected an error for mutation %q", bad)
			}
		})
	}
}

func TestMoveRequestUsesNativeSnapshotAndImmutableDestination(t *testing.T) {
	destination, _, row, _ := movementFixture()
	request := moveRequest(map[string]any{"mapId": 0.0}, map[string]any{"context": map[string]any{"nativeGeneration": "2"}, "leaseId": "lease"}, row, 4, destination)
	operation, _ := na.AsMap(request["operation"])
	movePawn, _ := na.AsMap(operation["movePawn"])
	want := map[string]any{"pawn": map[string]any{"entityId": "Human1", "expectedSnapshotToken": "native"}, "destination": map[string]any{"x": 2.0, "z": 0.0}}
	if !na.DeepEqual(movePawn, want) {
		t.Fatalf("unexpected movePawn operation: %#v", movePawn)
	}
	destination["x"] = 9.0
	movedDestination, _ := na.AsMap(movePawn["destination"])
	if na.AsNumber(movedDestination["x"]) != 2.0 {
		t.Fatalf("expected the request's destination to be immutable, got %#v", movedDestination)
	}
}
