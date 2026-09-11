package main

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
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

type identityOnly struct {
	calls  int
	paused *bool
	change bool
}

func (s *identityOnly) Identity(context.Context) (*l.IdentityReply, bridge.Result, error) {
	s.calls++
	tick := int64(10)
	if s.change {
		tick += int64(s.calls)
	}
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}, Tick: &tick}, Paused: s.paused}}}, bridge.Result{}, nil
}
func TestSampleRequiresOnlyPausedLifecycleIdentity(t *testing.T) {
	s := &identityOnly{paused: proto.Bool(true)}
	if _, err := takeSample(context.Background(), s); err != nil || s.calls != 2 {
		t.Fatalf("calls=%d err=%v", s.calls, err)
	}
	for _, s := range []*identityOnly{{}, {paused: proto.Bool(false)}, {paused: proto.Bool(true), change: true}} {
		if _, err := takeSample(context.Background(), s); err == nil {
			t.Fatal("unknown, unpaused, or changing identity accepted")
		}
	}
}
