package main

import (
	"bytes"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/observation"
	wire "github.com/davidarcher/RimGovernor/go/internal/wire/placementpreview"
)

const validFixture = `{"version":1,"placements":[{"defName":"SleepingSpot","x":7,"z":8,"rotation":"north","stuff":""}],"expected":["placeable"]}`

func TestFixtureUsesStrictGeneratedBatch(t *testing.T) {
	got, err := decodeFixture([]byte(validFixture))
	if err != nil || len(got.placements) != 1 || got.placements[0].X != 7 {
		t.Fatal(got, err)
	}
	for _, bad := range []string{
		`null`, validFixture + `{}`, `{"version":1,"version":1,"placements":[],"expected":[]}`,
		`{"version":2,"placements":[],"expected":[]}`,
		string(bytes.Replace([]byte(validFixture), []byte(`"x":7`), []byte(`"x":7.0`), 1)),
		string(bytes.Replace([]byte(validFixture), []byte(`"x":7`), []byte(`"x":7,"x":8`), 1)),
		string(bytes.Replace([]byte(validFixture), []byte(`"stuff":""`), []byte(`"stuff":null`), 1)),
		string(bytes.Replace([]byte(validFixture), []byte(`"placeable"`), []byte(`"anything"`), 1)),
		string(bytes.Replace([]byte(validFixture), []byte(`["placeable"]`), []byte(`[]`), 1)),
		string(bytes.Replace([]byte(validFixture), []byte(`"north"`), []byte(`"all"`), 1)),
		string(bytes.Replace([]byte(validFixture), []byte(`"version":1`), []byte(`"unknown":1,"version":1`), 1)),
	} {
		if _, err := decodeFixture([]byte(bad)); err == nil {
			t.Fatalf("accepted invalid fixture: %s", bad)
		}
	}
}
func TestCLIValidationBeforeConnection(t *testing.T) {
	if _, err := parseOptions(nil); err == nil {
		t.Fatal("missing paths accepted")
	}
	abs := filepath.Join(t.TempDir(), "file")
	args := []string{"-gabs", abs, "-config", abs, "-requests", abs, "-output", abs}
	if _, err := parseOptions(args); err != nil {
		t.Fatal(err)
	}
	if _, err := parseOptions(append(args, "unexpected")); err == nil {
		t.Fatal("positional argument accepted")
	}
	var out bytes.Buffer
	if code := run([]string{"-requests", "relative"}, &out, &out); code != 2 {
		t.Fatal(code)
	}
}
func TestReplyRequiresVersionOrderScopeAndExpectedOutcome(t *testing.T) {
	f, err := decodeFixture([]byte(validFixture))
	if err != nil {
		t.Fatal(err)
	}
	identity := observation.Identity{Map: 0, Tick: 20}
	good := func() wire.PreviewReply {
		return wire.PreviewReply{PreviewBatch: &wire.PreviewBatch{Version: 2, MapId: 0, Tick: 20, Success: true, Results: []wire.PreviewCandidate{{PreviewEvaluated: &wire.PreviewEvaluated{Success: true, CanPlace: true, Rotations: []wire.PreviewRotation{{Rotation: "north", Accepted: true, OccupiedCells: []wire.PreviewCell{{X: 7, Z: 8}}}}}}}}}
	}
	if _, err = checkReply(good(), f, identity); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*wire.PreviewReply){func(r *wire.PreviewReply) { r.PreviewBatch.Version = 1 }, func(r *wire.PreviewReply) { r.PreviewBatch.Tick++ }, func(r *wire.PreviewReply) { r.PreviewBatch.MapId++ }, func(r *wire.PreviewReply) { r.PreviewBatch.Results = nil }, func(r *wire.PreviewReply) { r.PreviewBatch.Results[0].PreviewEvaluated.Rotations[0].Rotation = "east" }, func(r *wire.PreviewReply) {
		r.PreviewBatch.Results[0].PreviewEvaluated.Rotations[0].OccupiedCells[0].X++
	}} {
		r := good()
		change(&r)
		if _, err = checkReply(r, f, identity); err == nil {
			t.Fatal("mismatched native result accepted")
		}
	}
	r := good()
	r.PreviewBatch.Results[0] = wire.PreviewCandidate{PreviewFailure: &wire.PreviewFailure{Error: "Unknown definition SleepingSpot"}}
	if _, err = checkReply(r, f, identity); err == nil {
		t.Fatal("candidate failure treated as placement success")
	}
	f.expected[0] = "invalid_definition"
	if _, err = checkReply(r, f, identity); err != nil {
		t.Fatal(err)
	}
	if _, err = checkReply(wire.PreviewReply{PreviewFailure: &wire.PreviewFailure{Error: "invalid request"}}, f, identity); err == nil {
		t.Fatal("request failure treated as candidate failure")
	}
}
