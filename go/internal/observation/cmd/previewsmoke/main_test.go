package main

import (
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestFixtureUsesOfficialProtoJSON(t *testing.T) {
	valid := []byte(`{"version":1,"placements":[{"defName":"Wall","x":7,"z":8,"rotation":"ROTATION_NORTH"}],"expected":["placeable"]}`)
	got, err := decodeFixture(valid)
	if err != nil || got.placements[0].GetX() != 7 {
		t.Fatalf("%+v %v", got, err)
	}
	for _, raw := range []string{`{"version":1,"placements":[],"expected":[]}`, `{"version":1,"placements":[{"defName":"Wall","x":0,"z":0,"rotation":"north"}],"expected":["placeable"]}`, `{"version":1,"placements":[{"defName":"Wall","x":0,"z":0,"rotation":"ROTATION_NORTH","godMode":true}],"expected":["placeable"]}`} {
		if _, err := decodeFixture([]byte(raw)); err == nil {
			t.Fatal("invalid fixture accepted")
		}
	}
}
func TestReplyChecksNativeOutcome(t *testing.T) {
	request := &p.PlacementCandidate{DefName: proto.String("Wall"), X: proto.Int32(7), Z: proto.Int32(8), Rotation: p.Rotation_ROTATION_NORTH.Enum()}
	f := fixture{placements: []*p.PlacementCandidate{request}, expected: []string{"placeable"}}
	reply := &p.PlacementReply{Outcome: &p.PlacementReply_Batch{Batch: &p.PlacementBatch{Context: &c.ObservationContext{Identity: &c.Identity{MapId: proto.Int32(0)}, Tick: proto.Int64(10)}, Results: []*p.CandidateReply{{Outcome: &p.CandidateReply_Evaluated{Evaluated: &p.PlacementEvaluated{CanPlace: proto.Bool(true), Rotations: []*p.PlacementRotation{{Rotation: p.Rotation_ROTATION_NORTH.Enum(), Accepted: proto.Bool(true), OccupiedCells: []*c.Cell{{X: proto.Int32(7), Z: proto.Int32(8)}}}}}}}}}}}
	if _, err := checkReply(reply, f, observation.Identity{Tick: 10}); err != nil {
		t.Fatal(err)
	}
	reply.GetBatch().Context.Tick = proto.Int64(11)
	if _, err := checkReply(reply, f, observation.Identity{Tick: 10}); err == nil {
		t.Fatal("changed tick accepted")
	}
}
