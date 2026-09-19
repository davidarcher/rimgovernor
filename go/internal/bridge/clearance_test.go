package bridge

import (
	"context"
	"errors"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func clearanceSnapshot() *o.ClearanceTargetsSnapshot {
	return &o.ClearanceTargetsSnapshot{Context: pbContext(), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}, Targets: []*o.ClearanceTarget{{EntityId: proto.String("Wall1"), DefName: proto.String("Wall"), Occupied: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Maximum: &c.Cell{X: proto.Int32(2), Z: proto.Int32(3)}}, Class: o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR, Deconstructible: proto.Bool(true), InHome: proto.Bool(false), AncientDanger: proto.Bool(true), RoofBlocker: proto.String("Unsupported roof"), Designated: proto.Bool(true), ControllerOwned: proto.Bool(false)}}}
}

func TestClearanceReadAndUnavailableStub(t *testing.T) {
	for _, stub := range []bool{false, true} {
		reply := &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: clearanceSnapshot()}}
		if stub {
			reply.Outcome = &o.ClearanceTargetsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}
		}
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
			if arg.Tool != clearanceTool {
				t.Fatal(arg.Tool)
			}
			draftTestRequest(t, arg, &o.ClearanceTargetsRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}})
			return pbResult(reply), nil
		}}, time.Second)
		got, _, err := client.ReadClearanceTargets(context.Background(), pbIdentity())
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
		"wrong load":    func(v *o.ClearanceTargetsSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"partial":       func(v *o.ClearanceTargetsSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"counts absent": func(v *o.ClearanceTargetsSnapshot) { v.Completeness.Matched = nil },
		"unreadable":    func(v *o.ClearanceTargetsSnapshot) { v.Completeness.Unreadable = proto.Uint64(1) },
		"duplicate": func(v *o.ClearanceTargetsSnapshot) {
			v.Targets = append(v.Targets, v.Targets[0])
			v.Completeness.Returned = proto.Uint64(2)
			v.Completeness.Matched = proto.Uint64(2)
		},
		"danger absent":          func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].AncientDanger = nil },
		"home absent":            func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].InHome = nil },
		"deconstructible absent": func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].Deconstructible = nil },
		"ownership absent":       func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].ControllerOwned = nil },
		"ownership without order": func(v *o.ClearanceTargetsSnapshot) {
			v.Targets[0].ControllerOwned = proto.Bool(true)
			v.Targets[0].Designated = proto.Bool(false)
		},
		"unknown class":  func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].Class = 99 },
		"bad rect":       func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].Occupied.Maximum.X = proto.Int32(0) },
		"oversized rect": func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].Occupied.Maximum.X = proto.Int32(2147483647) },
		"empty blocker":  func(v *o.ClearanceTargetsSnapshot) { v.Targets[0].RoofBlocker = proto.String("") },
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
	v.Completeness.Matched = proto.Uint64(0)
	v.Completeness.Returned = proto.Uint64(0)
	if err := ValidateClearanceTargets(v, pbIdentity()); err != nil {
		t.Fatal("complete empty census", err)
	}
}
