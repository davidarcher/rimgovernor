package bridge

import (
	"context"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func TestArrestPreviewReusesNativeOperation(t *testing.T) {
	command := &o.Arrest{Pawn: &o.EntityPrecondition{EntityId: proto.String("warden")}, Target: &o.EntityPrecondition{EntityId: proto.String("ancient")}, Bed: &o.EntityPrecondition{EntityId: proto.String("prison")}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(ctx context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		draftTestRequest(t, arg, &o.PreviewRequest{Identity: pbIdentity(), Operation: &o.Operation{Command: &o.Operation_Arrest{Arrest: command}}})
		return pbResult(&o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: buildingAdmission().AdmittedContext, Accepted: proto.Bool(true)}}}), nil
	}}, time.Second)
	if reply, _, err := client.PreviewArrest(context.Background(), pbIdentity(), command); err != nil || !reply.GetEvaluated().GetAccepted() {
		t.Fatal(reply, err)
	}
}

func TestArrestEvidenceRequiresExactBedAndOwnedClaim(t *testing.T) {
	expected := PawnOrderAttempt{PawnID: "warden", TargetID: "ancient", Kind: o.PawnOrderKind_PAWN_ORDER_KIND_CAPTURE, ArrestBed: "prison"}
	makeJob := func() *r.JobEffect {
		return &r.JobEffect{PawnId: proto.String("warden"), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "ancient"}}, TargetB: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "prison"}}, JobDef: proto.String("Arrest"), JobId: proto.Int32(42), Drafted: proto.Bool(true), DraftClaimId: proto.String("claim"), Issued: proto.Bool(true), Verified: proto.Bool(true), ResultingSnapshotToken: proto.String("after")}
	}
	effect := func(job *r.JobEffect) *r.EffectEvidence {
		return &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: job}}
	}
	if _, err := pawnOrderEvidence(effect(makeJob()), expected); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*r.JobEffect){
		func(j *r.JobEffect) { j.TargetB = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "other"}} },
		func(j *r.JobEffect) { j.DraftClaimId = nil },
		func(j *r.JobEffect) { j.JobDef = proto.String("Capture") },
		func(j *r.JobEffect) { j.PawnId = proto.String("other") },
	} {
		job := makeJob()
		change(job)
		if _, err := pawnOrderEvidence(effect(job), expected); err == nil {
			t.Fatal("mismatched arrest evidence accepted")
		}
	}
	expected.ArrestBed = ""
	if _, err := pawnOrderEvidence(effect(makeJob()), expected); err == nil {
		t.Fatal("ordinary Capture accepted Arrest evidence")
	}
}
