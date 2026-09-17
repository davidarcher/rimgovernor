package bridge

import (
	"context"
	"testing"
	"time"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

// The native AssignBed preview projects the pair plus whether the pawn is in
// that bed right now (BedEffect.sleeping); the #98 sleeping acceptance run
// refused every dispatch as a projection mismatch while the reader compared
// against a projection without it. Only the pair, previous bed and
// acceptance are the contract.
func TestPreviewBedAssignIgnoresProjectedSleeping(t *testing.T) {
	serve := func(effect *r.BedEffect) *Client {
		return testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
			return pbResult(&o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: buildingAdmission().AdmittedContext, Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Bed{Bed: effect}}}}}), nil
		}}, time.Second)
	}
	projected := func() *r.BedEffect {
		return &r.BedEffect{PawnId: proto.String("pawn"), BedId: proto.String("bed"), Assigned: proto.Bool(true), Sleeping: proto.Bool(false)}
	}
	previous := BedAssignPreviousBed{Clear: true}
	for name, effect := range map[string]*r.BedEffect{"awake": projected(), "sleeping": {PawnId: proto.String("pawn"), BedId: proto.String("bed"), Assigned: proto.Bool(true), Sleeping: proto.Bool(true)}, "unreported": {PawnId: proto.String("pawn"), BedId: proto.String("bed"), Assigned: proto.Bool(true)}} {
		reply, _, err := serve(effect).PreviewBedAssign(context.Background(), pbIdentity(), "pawn", "pawn-token", "bed", "bed-token", previous)
		if err != nil || !reply.GetEvaluated().GetAccepted() {
			t.Fatalf("%s: %v %v", name, reply, err)
		}
	}
	for name, change := range map[string]func(*r.BedEffect){
		"other bed":     func(v *r.BedEffect) { v.BedId = proto.String("other") },
		"other pawn":    func(v *r.BedEffect) { v.PawnId = proto.String("other") },
		"previous kept": func(v *r.BedEffect) { v.PreviousBedId = proto.String("old") },
		"not assigned":  func(v *r.BedEffect) { v.Assigned = proto.Bool(false) },
	} {
		effect := projected()
		change(effect)
		if _, _, err := serve(effect).PreviewBedAssign(context.Background(), pbIdentity(), "pawn", "pawn-token", "bed", "bed-token", previous); err == nil {
			t.Fatalf("%s: accepted", name)
		}
	}
}
