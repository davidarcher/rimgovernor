package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func clearanceSnapshot() *o.ClearanceTargetsSnapshot {
	return &o.ClearanceTargetsSnapshot{Context: pbContext(), Targets: []*o.ClearanceTarget{{EntityId: proto.String("Wall1"), DefName: proto.String("Wall"), Occupied: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Maximum: &c.Cell{X: proto.Int32(2), Z: proto.Int32(3)}}, Class: o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR, Deconstructible: proto.Bool(true), InHome: proto.Bool(false), AncientDanger: proto.Bool(true), RoofBlocker: proto.String("Unsupported roof"), Designated: proto.Bool(true)}}}
}

func TestClearanceReadAndUnavailableStub(t *testing.T) {
	for _, stub := range []bool{false, true} {
		reply := &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: clearanceSnapshot()}}
		if stub {
			reply.Outcome = &o.ClearanceTargetsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}
		}
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
			if arg.Tool != clearanceTool {
				t.Fatal(arg.Tool)
			}
			protoTestRequest(t, arg, &o.ClearanceTargetsRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, IncludeSalvage: true})
			return pbResult(reply), nil
		}}, time.Second)
		got, _, err := client.ReadClearanceTargets(context.Background(), pbIdentity(), true)
		if stub {
			if !errors.Is(err, ErrUnavailable) {
				t.Fatal(err)
			}
			continue
		}
		if err != nil || !proto.Equal(reply, got) {
			t.Fatal(got, err)
		}
	}
}

func TestClearanceRejectsIncompleteAndUnsafeDefaults(t *testing.T) {
	for name, edit := range map[string]func(*o.ClearanceTargetsSnapshot){
		"wrong load": func(v *o.ClearanceTargetsSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"duplicate": func(v *o.ClearanceTargetsSnapshot) {
			v.Targets = append(v.Targets, v.Targets[0])
		},
		"danger absent":          func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].AncientDanger = nil },
		"home absent":            func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].InHome = nil },
		"deconstructible absent": func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].Deconstructible = nil },
		"unknown class":          func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].Class = 99 },
		"bad rect":               func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].Occupied.Maximum.X = proto.Int32(0) },
		"oversized rect":         func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].Occupied.Maximum.X = proto.Int32(2147483647) },
		"empty blocker":          func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].RoofBlocker = proto.String("") },
	} {
		t.Run(name, func(t *testing.T) {
			v := clearanceSnapshot()
			edit(v)
			if ValidateClearanceTargets(v, pbIdentity()) == nil {
				t.Fatal("invalid census accepted")
			}
		})
	}
	v := clearanceSnapshot()
	v.Targets = nil
	if err := ValidateClearanceTargets(v, pbIdentity()); err != nil {
		t.Fatal("complete empty census", err)
	}
}

func TestClearanceChunkValidation(t *testing.T) {
	chunk := func(id string) *o.ClearanceChunk {
		return &o.ClearanceChunk{EntityId: proto.String(id), DefName: proto.String("ChunkGranite"), Cell: &c.Cell{X: proto.Int32(3), Z: proto.Int32(4)}, Forbidden: proto.Bool(false), Stored: proto.Bool(false), Destination: proto.Bool(false)}
	}
	v := clearanceSnapshot()
	v.Chunks = []*o.ClearanceChunk{chunk("c1"), chunk("c2")}
	if err := ValidateClearanceTargets(v, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	for name, change := range map[string]func(*o.ClearanceTargetsSnapshot){
		"duplicate chunk": func(v *o.ClearanceTargetsSnapshot) { v.Chunks[1].EntityId = proto.String("c1") },
		"stored destination": func(v *o.ClearanceTargetsSnapshot) {
			v.Chunks[0].Stored = proto.Bool(true)
			v.Chunks[0].Destination = proto.Bool(true)
		},
		"absent forbidden": func(v *o.ClearanceTargetsSnapshot) { v.Chunks[0].Forbidden = nil },
		"missing cell":     func(v *o.ClearanceTargetsSnapshot) { v.Chunks[0].Cell = nil },
	} {
		v := clearanceSnapshot()
		v.Chunks = []*o.ClearanceChunk{chunk("c1"), chunk("c2")}
		change(v)
		if ValidateClearanceTargets(v, pbIdentity()) == nil {
			t.Fatal(name)
		}
	}
}

func TestClearancePlannedGroundRequestAndFloors(t *testing.T) {
	rect := func(x0, z0, x1, z1 int32) *o.Rectangle {
		return &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(x0), Z: proto.Int32(z0)}, Maximum: &c.Cell{X: proto.Int32(x1), Z: proto.Int32(z1)}}
	}
	planned := []*o.Rectangle{rect(10, 10, 12, 12)}
	floor := func(x, z int32) *o.ClearanceFloor {
		return &o.ClearanceFloor{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, DefName: proto.String("WoodPlankFloor"), Designated: proto.Bool(false)}
	}
	for name, tc := range map[string]struct {
		floors []*o.ClearanceFloor
		ok     bool
	}{
		"per cell":      {[]*o.ClearanceFloor{floor(10, 10), floor(12, 12)}, true},
		"outside":       {[]*o.ClearanceFloor{floor(13, 10)}, false},
		"duplicate":     {[]*o.ClearanceFloor{floor(11, 11), floor(11, 11)}, false},
		"no def":        {[]*o.ClearanceFloor{{Cell: &c.Cell{X: proto.Int32(11), Z: proto.Int32(11)}, Designated: proto.Bool(true)}}, false},
		"no designated": {[]*o.ClearanceFloor{{Cell: &c.Cell{X: proto.Int32(11), Z: proto.Int32(11)}, DefName: proto.String("TileSandstone")}}, false},
	} {
		snapshot := clearanceSnapshot()
		snapshot.Targets[0].EnclosesRoom = proto.Bool(true)
		snapshot.Floors = tc.floors
		reply := &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: snapshot}}
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
			protoTestRequest(t, arg, &o.ClearanceTargetsRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, PlannedGround: planned})
			return pbResult(reply), nil
		}}, time.Second)
		got, _, err := client.ReadClearanceTargetsOnGround(context.Background(), pbIdentity(), false, planned)
		if tc.ok != (err == nil) || tc.ok && !proto.Equal(got, reply) {
			t.Fatal(name, got, err)
		}
	}
	// Without planned ground no floor row is admissible.
	snapshot := clearanceSnapshot()
	snapshot.Floors = []*o.ClearanceFloor{floor(10, 10)}
	if validatePlannedFloors(snapshot, nil) == nil {
		t.Fatal("floor accepted without planned ground")
	}
}

func TestClearanceRejectsInvalidPlannedGround(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		t.Error("invalid request sent")
		return nil, nil
	}}, time.Second)
	bad := &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(5), Z: proto.Int32(5)}, Maximum: &c.Cell{X: proto.Int32(4), Z: proto.Int32(5)}}
	if _, _, err := client.ReadClearanceTargetsOnGround(context.Background(), pbIdentity(), false, []*o.Rectangle{bad}); err == nil {
		t.Fatal("inverted rectangle accepted")
	}
}
