package bridge

import (
	"context"
	"encoding/json"
	"errors"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func buildingPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func buildingEffect() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: &r.ConstructionEffect{OriginThingId: proto.String("blueprint1"), CurrentThingId: proto.String("blueprint1"), DefName: proto.String("Wall"), Stuff: proto.String(""), Cell: &c.Cell{X: proto.Int32(0), Z: proto.Int32(0)}, Rotation: pbRequest().Placements[0].Rotation, Stage: r.ConstructionStage_CONSTRUCTION_STAGE_BLUEPRINT.Enum(), Present: proto.Bool(true), Started: proto.Bool(false), Failed: proto.Bool(false)}}}
}
func buildingAdmission() *r.Receipt {
	return &r.Receipt{Attempt: buildingPre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: buildingEffect()}}}
}
func buildingDone() *r.Progress {
	e := buildingEffect()
	e.GetConstruction().Stage = r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING.Enum()
	e.GetConstruction().CurrentThingId = proto.String("building1")
	e.GetConstruction().Started = proto.Bool(true)
	return &r.Progress{Attempt: buildingPre().Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(2)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: e}}}
}
func TestBuildingCapabilityAndReceiptCorrelation(t *testing.T) {
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/operations_execute" {
			t.Fatal(args.Tool)
		}
		var wrapper struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(args.Arguments, &wrapper); err != nil {
			t.Fatal(err)
		}
		req := &o.ExecuteRequest{}
		if err := protojson.Unmarshal([]byte(wrapper.Request), req); err != nil {
			t.Fatal(err)
		}
		if !proto.Equal(req.Precondition, buildingPre()) || !proto.Equal(req.Operation.GetPlaceBuilding().Placement, pbRequest().Placements[0]) {
			t.Fatal("request correlation lost")
		}
		return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: buildingAdmission()}}), nil
	}}
	cap, err := NewBuildingControl(testClient(t, s, time.Second))
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := cap.PlaceBuilding(context.Background(), buildingPre(), pbRequest().Placements[0])
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	if reply.GetReceipt().GetApplied().Observed.GetConstruction().GetStage() != r.ConstructionStage_CONSTRUCTION_STAGE_BLUEPRINT {
		t.Fatal("receipt promoted completion")
	}
	for _, change := range []func(*r.Receipt){func(v *r.Receipt) { v.Attempt.AttemptId = proto.Uint64(2) }, func(v *r.Receipt) { v.AdmittedContext.Identity.LoadToken = proto.String("other") }, func(v *r.Receipt) { v.AdmittedContext.NativeGeneration = proto.Uint64(2) }, func(v *r.Receipt) {
		v.GetApplied().Observed = &r.EffectEvidence{Effect: &r.EffectEvidence_Designation{Designation: &r.DesignationEffect{}}}
	}, func(v *r.Receipt) { v.GetApplied().Observed.GetConstruction().Cell.X = proto.Int32(2) }, func(v *r.Receipt) { v.GetApplied().Observed.GetConstruction().OriginThingId = nil }, func(v *r.Receipt) { v.GetApplied().Observed.GetConstruction().Present = nil }} {
		v := buildingAdmission()
		change(v)
		if err := buildingReceipt(v, buildingPre(), pbRequest().Placements[0]); !errors.Is(err, ErrContract) {
			t.Fatal("invalid receipt accepted", err)
		}
	}
}
func TestBuildingInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	cap, _ := NewBuildingControl(testClient(t, s, time.Second))
	for _, change := range []func(*a.WritePrecondition){func(v *a.WritePrecondition) { v.ExpectedGeneration = nil }, func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil }, func(v *a.WritePrecondition) { v.Identity.ProtoReflect().SetUnknown([]byte{0x20, 1}) }, func(v *a.WritePrecondition) { v.Attempt.ControllerSessionId = proto.String("bad\x00") }} {
		v := buildingPre()
		change(v)
		if _, _, err := cap.PlaceBuilding(context.Background(), v, pbRequest().Placements[0]); !errors.Is(err, ErrContract) {
			t.Fatal("invalid input accepted", err)
		}
	}
	candidate := pbRequest().Placements[0]
	candidate.ProtoReflect().SetUnknown([]byte{0x38, 1})
	if _, _, err := cap.PlaceBuilding(context.Background(), buildingPre(), candidate); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched")
	}
}
func TestBuildingProgressRequiresCausalCompleteLineage(t *testing.T) {
	if err := buildingProgress(buildingDone(), buildingAdmission(), pbRequest().Placements[0]); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*r.Progress){func(v *r.Progress) { v.Attempt.ActionId = proto.String("other") }, func(v *r.Progress) { v.Context.Identity.MapId = proto.Int32(1) }, func(v *r.Progress) { v.Context.Tick = proto.Int64(9) }, func(v *r.Progress) { v.CompleteInspection = nil }, func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) }, func(v *r.Progress) {
		v.GetCompleted().Evidence.GetConstruction().OriginThingId = proto.String("replacement")
	}, func(v *r.Progress) {
		v.GetCompleted().Evidence.GetConstruction().Stage = r.ConstructionStage_CONSTRUCTION_STAGE_FRAME.Enum()
	}, func(v *r.Progress) { v.GetCompleted().Evidence.GetConstruction().Stuff = nil }} {
		v := buildingDone()
		change(v)
		if err := buildingProgress(v, buildingAdmission(), pbRequest().Placements[0]); !errors.Is(err, ErrContract) {
			t.Fatal("invalid progress accepted", err)
		}
	}
	admission := buildingAdmission()
	admission.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{}}
	if err := buildingReceipt(admission, buildingPre(), pbRequest().Placements[0]); err != nil {
		t.Fatal("truthful uncertainty rejected", err)
	}
	if err := buildingProgress(buildingDone(), admission, pbRequest().Placements[0]); err != nil {
		t.Fatal("ledger-established lineage rejected", err)
	}
	absent := buildingDone()
	absent.Effect = &r.Progress_Absent{Absent: &r.AbsentEffect{InspectionToken: proto.String("inspection")}}
	if err := buildingProgress(absent, admission, pbRequest().Placements[0]); err != nil {
		t.Fatal(err)
	}
	absent.CompleteInspection = proto.Bool(false)
	if err := buildingProgress(absent, admission, pbRequest().Placements[0]); err == nil {
		t.Fatal("incomplete absence accepted")
	}
}
func TestBuildingReadMethodsAndUncertainty(t *testing.T) {
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		switch args.Tool {
		case "rimgovernor/receipts_lookup":
			return pbResult(&r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: buildingDone().Context}}}), nil
		case "rimgovernor/receipts_observe_progress":
			return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: buildingDone()}}), nil
		default:
			t.Fatal("unexpected mutation", args.Tool)
			return nil, nil
		}
	}}
	client := testClient(t, s, time.Second)
	lookup, _, err := client.LookupBuildingAttempt(context.Background(), buildingPre().Identity, buildingPre().Attempt, 1, pbRequest().Placements[0])
	if err != nil || lookup.GetUnknown() == nil {
		t.Fatal(err)
	}
	if _, _, err = client.ObserveBuildingProgress(context.Background(), buildingAdmission(), pbRequest().Placements[0]); err != nil {
		t.Fatal(err)
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	cap, _ := NewBuildingControl(testClient(t, refusal, time.Second))
	reply, raw, err := cap.PlaceBuilding(context.Background(), buildingPre(), pbRequest().Placements[0])
	if !errors.Is(err, ErrRefused) || reply.GetFailure() == nil || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	cap, _ = NewBuildingControl(testClient(t, lost, time.Second))
	reply, _, err = cap.PlaceBuilding(context.Background(), buildingPre(), pbRequest().Placements[0])
	if err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}
