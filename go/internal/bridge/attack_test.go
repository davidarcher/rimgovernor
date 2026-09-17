package bridge

import (
	"context"
	"errors"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

// gotoTestProgress is a completed pawn-job progress reply (a Goto job that
// has already been issued and finished); the attack boundary must reject it
// because it carries no attack evidence.
func gotoTestProgress() *r.Progress {
	j := &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("Goto"), JobId: proto.Int32(42), TargetA: &r.JobTarget{Target: &r.JobTarget_Cell{Cell: &c.Cell{X: proto.Int32(4), Z: proto.Int32(5)}}}, CanTry: proto.Bool(true), Issued: proto.Bool(false), Verified: proto.Bool(true)}
	return &r.Progress{Attempt: buildingPre().Attempt, Context: buildingAdmission().AdmittedContext, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: j}}}}}
}
func attackTestCommand() *o.AttackTarget {
	return &o.AttackTarget{Pawn: draftTestPawn(), Target: &o.EntityPrecondition{EntityId: proto.String("enemy"), ExpectedSnapshotToken: proto.String("enemy-before")}, Mode: o.AttackMode_ATTACK_MODE_MELEE.Enum(), RequireHostile: proto.Bool(true), RequireStanding: proto.Bool(true), RequireCombatHealth: proto.Bool(true)}
}
func attackTestAttempt() AttackAttempt {
	d := draftTestAttempt()
	return AttackAttempt{d.Identity, d.Attempt, d.NativeGeneration, d.PawnID, "enemy", o.AttackMode_ATTACK_MODE_MELEE, true, true, true}
}
func attackTestReceipt() *r.Receipt {
	v := draftTestReceipt()
	j := draftObserved(v)
	j.JobId = proto.Int32(42)
	j.JobDef = proto.String("AttackMelee")
	j.TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "enemy"}}
	return v
}
func attackTestProgress() *r.Progress {
	v := gotoTestProgress()
	j := proto.Clone(draftObserved(attackTestReceipt())).(*r.JobEffect)
	j.Issued = proto.Bool(false)
	v.GetCompleted().Evidence = &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: j}}
	return v
}
func TestAttackFixedMeleeSDK(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		switch arg.Tool {
		case "rimgovernor/operations_preview":
			draftTestRequest(t, arg, &o.PreviewRequest{Identity: pbIdentity(), Operation: attackOperation(attackTestCommand())})
			return pbResult(&o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: buildingAdmission().AdmittedContext, Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("AttackMelee"), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "enemy"}}, CanTry: proto.Bool(true), Issued: proto.Bool(false), Verified: proto.Bool(false)}}}}}}), nil
		case "rimgovernor/operations_execute":
			draftTestRequest(t, arg, &o.ExecuteRequest{Precondition: buildingPre(), Operation: attackOperation(attackTestCommand())})
			return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: attackTestReceipt()}}), nil
		case "rimgovernor/receipts_lookup":
			draftTestRequest(t, arg, &r.LookupRequest{Identity: pbIdentity(), Attempt: buildingPre().Attempt})
			return pbResult(&r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: attackTestReceipt()}}), nil
		case "rimgovernor/receipts_observe_progress":
			draftTestRequest(t, arg, &r.ProgressRequest{Identity: pbIdentity(), Attempt: buildingPre().Attempt})
			return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: attackTestProgress()}}), nil
		}
		t.Fatal(arg.Tool)
		return nil, nil
	}}, time.Second)
	if _, _, err := client.PreviewAttack(context.Background(), pbIdentity(), attackTestCommand()); err != nil {
		t.Fatal(err)
	}
	control, _ := NewAttackControl(client)
	receipt, _, err := control.AttackTarget(context.Background(), buildingPre(), attackTestCommand())
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = client.LookupAttackAttempt(context.Background(), attackTestAttempt()); err != nil {
		t.Fatal(err)
	}
	if _, _, err = client.ObserveAttackProgress(context.Background(), attackTestAttempt(), receipt.GetReceipt()); err != nil {
		t.Fatal(err)
	}
	if calls != 4 {
		t.Fatal(calls)
	}
}
func rangedTestCommand() *o.AttackTarget {
	return &o.AttackTarget{Pawn: draftTestPawn(), Target: &o.EntityPrecondition{EntityId: proto.String("enemy"), ExpectedSnapshotToken: proto.String("enemy-before")}, Mode: o.AttackMode_ATTACK_MODE_RANGED.Enum(), RequireHostile: proto.Bool(true), RequireStanding: proto.Bool(true), RequireCombatHealth: proto.Bool(true)}
}
func rangedTestAttempt() AttackAttempt {
	d := draftTestAttempt()
	return AttackAttempt{d.Identity, d.Attempt, d.NativeGeneration, d.PawnID, "enemy", o.AttackMode_ATTACK_MODE_RANGED, true, true, true}
}
func rangedTestReceipt() *r.Receipt {
	v := draftTestReceipt()
	j := draftObserved(v)
	j.JobId = proto.Int32(42)
	j.JobDef = proto.String("AttackStatic")
	j.TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "enemy"}}
	return v
}
func TestAttackFixedRangedSDK(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		switch arg.Tool {
		case "rimgovernor/operations_preview":
			draftTestRequest(t, arg, &o.PreviewRequest{Identity: pbIdentity(), Operation: attackOperation(rangedTestCommand())})
			return pbResult(&o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: buildingAdmission().AdmittedContext, Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("AttackStatic"), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "enemy"}}, CanTry: proto.Bool(true), Issued: proto.Bool(false), Verified: proto.Bool(false)}}}}}}), nil
		case "rimgovernor/operations_execute":
			draftTestRequest(t, arg, &o.ExecuteRequest{Precondition: buildingPre(), Operation: attackOperation(rangedTestCommand())})
			return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: rangedTestReceipt()}}), nil
		case "rimgovernor/receipts_lookup":
			draftTestRequest(t, arg, &r.LookupRequest{Identity: pbIdentity(), Attempt: buildingPre().Attempt})
			return pbResult(&r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: rangedTestReceipt()}}), nil
		case "rimgovernor/receipts_observe_progress":
			draftTestRequest(t, arg, &r.ProgressRequest{Identity: pbIdentity(), Attempt: buildingPre().Attempt})
			progress := gotoTestProgress()
			j := proto.Clone(draftObserved(rangedTestReceipt())).(*r.JobEffect)
			j.Issued = proto.Bool(false)
			progress.GetCompleted().Evidence = &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: j}}
			return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: progress}}), nil
		}
		t.Fatal(arg.Tool)
		return nil, nil
	}}, time.Second)
	if _, _, err := client.PreviewAttack(context.Background(), pbIdentity(), rangedTestCommand()); err != nil {
		t.Fatal(err)
	}
	control, _ := NewAttackControl(client)
	receipt, _, err := control.AttackTarget(context.Background(), buildingPre(), rangedTestCommand())
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = client.LookupAttackAttempt(context.Background(), rangedTestAttempt()); err != nil {
		t.Fatal(err)
	}
	if _, _, err = client.ObserveAttackProgress(context.Background(), rangedTestAttempt(), receipt.GetReceipt()); err != nil {
		t.Fatal(err)
	}
	if calls != 4 {
		t.Fatal(calls)
	}
}
func TestAttackCompletionAfterManualAndLostReadback(t *testing.T) {
	for _, uncertain := range []bool{false, true} {
		for _, changed := range []bool{false, true} {
			original := attackTestReceipt()
			if uncertain {
				original.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{Detail: proto.String("readback lost")}}
			}
			progress := attackTestProgress()
			if changed {
				progress.Context.NativeGeneration = proto.Uint64(9)
				j := progress.GetCompleted().Evidence.GetJob()
				j.Drafted = proto.Bool(false)
				j.DraftClaimId = nil
			}
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: progress}}), nil
			}}, time.Second)
			got, _, err := client.ObserveAttackProgress(context.Background(), attackTestAttempt(), original)
			if err != nil || got.GetProgress().GetCompleted() == nil {
				t.Fatal(uncertain, changed, got, err)
			}
		}
	}
}
func TestAttackGuardedInputsNeverCall(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, _ nativeArgument) (*mcp.CallToolResult, error) { calls++; return nil, nil }}, time.Second)
	control, _ := NewAttackControl(client)
	for name, edit := range map[string]func(*o.AttackTarget){"auto": func(v *o.AttackTarget) { v.Mode = o.AttackMode_ATTACK_MODE_AUTO.Enum() }, "unspecified": func(v *o.AttackTarget) { v.Mode = o.AttackMode_ATTACK_MODE_UNSPECIFIED.Enum() }, "missing guard": func(v *o.AttackTarget) { v.RequireHostile = nil }, "same target": func(v *o.AttackTarget) { v.Target.EntityId = proto.String("pawn") }, "target CAS": func(v *o.AttackTarget) { v.Target.ExpectedSnapshotToken = nil }, "unknown field": func(v *o.AttackTarget) { v.ProtoReflect().SetUnknown([]byte{0xa0, 6, 1}) }} {
		t.Run(name, func(t *testing.T) {
			v := attackTestCommand()
			edit(v)
			if _, _, err := client.PreviewAttack(context.Background(), pbIdentity(), v); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
			if _, _, err := control.AttackTarget(context.Background(), buildingPre(), v); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	if calls != 0 {
		t.Fatal(calls)
	}
}
func TestAttackRejectsMismatchedEvidence(t *testing.T) {
	for name, edit := range map[string]func(*r.Progress){"target": func(v *r.Progress) {
		v.GetCompleted().Evidence.GetJob().TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "other"}}
	}, "job": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().JobId = proto.Int32(99) }, "ranged": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().JobDef = proto.String("AttackStatic") }, "claim": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().DraftClaimId = proto.String("other") }, "unverified": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().Verified = proto.Bool(false) }, "inspection": func(v *r.Progress) { v.CompleteInspection = nil }, "attempt": func(v *r.Progress) { v.Attempt.ActionId = proto.String("other") }} {
		t.Run(name, func(t *testing.T) {
			v := attackTestProgress()
			edit(v)
			if err := attackProgress(v, attackTestAttempt(), attackTestReceipt()); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	if err := attackProgress(attackTestProgress(), attackTestAttempt(), nil); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	v := attackTestReceipt()
	v.Attempt.AttemptId = proto.Uint64(99)
	if err := attackReceipt(v, attackTestAttempt()); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	v = attackTestReceipt()
	v.Outcome = &r.Receipt_NoChange{NoChange: &r.NoChange{Observed: v.GetApplied().Observed}}
	if err := attackReceipt(v, attackTestAttempt()); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
func TestAttackNonCompletionOutcomesRemainDistinct(t *testing.T) {
	for _, kind := range []string{"pending", "interrupted", "target_dead", "unknown"} {
		t.Run(kind, func(t *testing.T) {
			v := attackTestProgress()
			e := v.GetCompleted().Evidence
			switch kind {
			case "pending":
				v.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: e}}
			case "unknown":
				v.CompleteInspection = proto.Bool(false)
				v.Effect = &r.Progress_Unknown{Unknown: &r.UnknownEffect{Reason: proto.String("no causal evidence")}}
			default:
				reason := r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED
				if kind == "target_dead" {
					reason = r.UnsuccessfulReason_UNSUCCESSFUL_REASON_TARGET_DEAD
				}
				e.GetJob().Verified = proto.Bool(false)
				v.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: reason.Enum(), Evidence: e}}
			}
			if err := attackProgress(v, attackTestAttempt(), attackTestReceipt()); err != nil {
				t.Fatal(err)
			}
			if v.GetCompleted() != nil {
				t.Fatal("invented completion")
			}
		})
	}
}
