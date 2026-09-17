package bill

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type billBoundaryFixture struct {
	read                                                  bridge.BillRead
	readErr                                               error
	preview                                               *op.PreviewReply
	previewErr                                            error
	emergency                                             bridge.EmergencyObservation
	emergencyErr                                          error
	lookupReply                                           *r.LookupReply
	lookupErr                                             error
	observeReply                                          *r.ProgressReply
	observeErr                                            error
	addReply                                              *op.ExecuteReply
	addErr                                                error
	reads, previews, emergencies, lookups, observes, adds int
	lastPre                                               *a.WritePrecondition
}

func (f *billBoundaryFixture) ReadBillTarget(context.Context, *c.Identity, string) (bridge.BillRead, bridge.Result, error) {
	f.reads++
	return f.read, bridge.Result{}, f.readErr
}
func (f *billBoundaryFixture) PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error) {
	f.previews++
	return f.preview, bridge.Result{}, f.previewErr
}
func (f *billBoundaryFixture) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	f.emergencies++
	return f.emergency, bridge.Result{}, f.emergencyErr
}
func (f *billBoundaryFixture) LookupBill(context.Context, bridge.BillAttempt) (*r.LookupReply, bridge.Result, error) {
	f.lookups++
	return f.lookupReply, bridge.Result{}, f.lookupErr
}
func (f *billBoundaryFixture) ObserveBill(context.Context, bridge.BillAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	f.observes++
	return f.observeReply, bridge.Result{}, f.observeErr
}
func (f *billBoundaryFixture) AddBill(_ context.Context, pre *a.WritePrecondition, _ domain.ProductionBill) (*op.ExecuteReply, bridge.Result, error) {
	f.adds++
	f.lastPre = pre
	return f.addReply, bridge.Result{}, f.addErr
}

func newBillBoundaryFixture(t *testing.T) (*BillBoundary, *billBoundaryFixture, executor.Target, executor.Placement, domain.ProductionBill) {
	t.Helper()
	base, bf := boundary.NewFixture(t)
	bill, err := domain.NewProductionBill("stove", "CookMealSimple", "before-token", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("action", bill)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := bf.Placement.Snapshot
	target := executor.Target{Action: action, Snapshot: snapshot}
	placement := executor.Placement{Action: action, Snapshot: snapshot, Attempt: 1, Tick: 10}
	ctx := &c.ObservationContext{Identity: boundary.Identity(snapshot), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}
	f := &billBoundaryFixture{
		read: bridge.BillRead{Context: proto.Clone(ctx).(*c.ObservationContext), Token: "before-token"},
		preview: &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
			Context: proto.Clone(ctx).(*c.ObservationContext), Accepted: proto.Bool(true),
		}}},
		emergency: bridge.EmergencyObservation{Context: proto.Clone(ctx).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}},
	}
	bb := &BillBoundary{Boundary: base, bill: BillCapabilities{Native: f, Writer: f}}
	return bb, f, target, placement, bill
}

func billEffectEvidence(bill domain.ProductionBill) *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Bill{Bill: &r.BillEffect{
		Stack:                &r.SnapshotEvidence{EntityId: proto.String(bill.Bench()), BeforeToken: proto.String(bill.BeforeToken()), AfterToken: proto.String("after-token")},
		BillId:               proto.String("bill1"),
		RecipeDef:            proto.String(bill.Recipe()),
		Present:              proto.Bool(true),
		Index:                proto.Int32(0),
		OrderedBillIds:       []string{"bill1"},
		ConfigurationMatches: proto.Bool(true),
		Iterations:           proto.Uint32(1),
		OutputComplete:       proto.Bool(true),
		OutputObserved:       proto.Bool(true),
		Outputs:              []*r.ProductionOutput{{ThingId: proto.String("meal1"), DefName: proto.String("MealSimple"), Units: proto.Int32(1)}},
	}}}
}

func TestInspectBillAcceptsMatchingTripleReadTicks(t *testing.T) {
	bb, f, target, _, _ := newBillBoundaryFixture(t)
	out, err := bb.InspectBill(context.Background(), target)
	if err != nil || !out.Accepted || out.Tick != 11 || f.reads != 1 || f.previews != 1 || f.emergencies != 1 {
		t.Fatal(err, out)
	}
}

func TestInspectBillRejections(t *testing.T) {
	for name, change := range map[string]func(*billBoundaryFixture){
		"stale token":          func(f *billBoundaryFixture) { f.read.Token = "other" },
		"preview not accepted": func(f *billBoundaryFixture) { f.preview.GetEvaluated().Accepted = proto.Bool(false) },
		"projected present": func(f *billBoundaryFixture) {
			f.preview.GetEvaluated().Projected = &r.EffectEvidence{}
		},
		"read tick mismatch":      func(f *billBoundaryFixture) { f.read.Context.Tick = proto.Int64(9) },
		"emergency tick mismatch": func(f *billBoundaryFixture) { f.emergency.Context.Tick = proto.Int64(9) },
		"foreign world":           func(f *billBoundaryFixture) { f.read.Context.Identity.LoadToken = proto.String("other") },
	} {
		t.Run(name, func(t *testing.T) {
			bb, f, target, _, _ := newBillBoundaryFixture(t)
			change(f)
			out, err := bb.InspectBill(context.Background(), target)
			if err == nil || out.Accepted {
				t.Fatal("invalid inspection accepted", err)
			}
		})
	}
}

func TestAddBillDispatchAndSnapshotMismatch(t *testing.T) {
	bb, f, _, placement, bill := newBillBoundaryFixture(t)
	admission := &r.Receipt{Attempt: bb.Attempt(placement), AdmittedContext: &c.ObservationContext{Identity: boundary.Identity(placement.Snapshot), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: billEffectEvidence(bill)}}}
	f.addReply = &op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}
	out, err := bb.AddBill(context.Background(), executor.BillDispatch{Attempt: placement, SnapshotToken: bill.BeforeToken()})
	if err != nil || out.Kind != domain.ReceiptAccepted || f.adds != 1 {
		t.Fatal(err, out)
	}
	bb, f, _, placement, _ = newBillBoundaryFixture(t)
	if _, err = bb.AddBill(context.Background(), executor.BillDispatch{Attempt: placement, SnapshotToken: "stale"}); !errors.Is(err, executor.ErrEvidence) || f.adds != 0 {
		t.Fatal("stale snapshot token dispatched", err)
	}
}

func TestObserveBillCompletedUnsuccessfulAndAbsent(t *testing.T) {
	bb, f, _, placement, bill := newBillBoundaryFixture(t)
	admission := &r.Receipt{Attempt: bb.Attempt(placement), AdmittedContext: &c.ObservationContext{Identity: boundary.Identity(placement.Snapshot), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Uncertain{Uncertain: &r.Uncertain{}}}
	f.lookupReply = &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: admission}}
	progressCtx := &c.ObservationContext{Identity: boundary.Identity(placement.Snapshot), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}
	attempt := bb.Attempt(placement)
	f.observeReply = &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: &r.Progress{Attempt: attempt, Context: progressCtx, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: billEffectEvidence(bill)}}}}}
	out, err := bb.ObserveBill(context.Background(), placement, placement.Snapshot)
	if err != nil || out.Observation.Effect != domain.EffectCompleted || !out.Complete || out.Iterations != 1 || out.OutputCount != 1 {
		t.Fatal(err, out)
	}
	unsuccessful := proto.Clone(billEffectEvidence(bill)).(*r.EffectEvidence)
	unsuccessful.GetBill().Present = proto.Bool(false)
	unsuccessful.GetBill().Index = proto.Int32(-1)
	unsuccessful.GetBill().OrderedBillIds = nil
	unsuccessful.GetBill().ConfigurationMatches = proto.Bool(false)
	unsuccessful.GetBill().Iterations = proto.Uint32(0)
	unsuccessful.GetBill().OutputComplete = proto.Bool(false)
	unsuccessful.GetBill().OutputObserved = proto.Bool(false)
	unsuccessful.GetBill().Outputs = nil
	f.observeReply = &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: &r.Progress{Attempt: attempt, Context: progressCtx, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED.Enum(), Evidence: unsuccessful}}}}}
	out, err = bb.ObserveBill(context.Background(), placement, placement.Snapshot)
	if err != nil || out.Observation.Effect != domain.EffectUnsuccessful || out.Observation.UnsuccessfulReason != domain.OutcomeNotAchieved {
		t.Fatal(err, out)
	}
	f.observeReply = &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: &r.Progress{Attempt: attempt, Context: progressCtx, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Absent{Absent: &r.AbsentEffect{InspectionToken: proto.String("inspection")}}}}}
	out, err = bb.ObserveBill(context.Background(), placement, placement.Snapshot)
	if err != nil || out.Observation.Effect != domain.EffectAbsent {
		t.Fatal(err, out)
	}
}

func TestObserveBillRejectsInvalidEvidenceAndUnknownAdmission(t *testing.T) {
	bb, f, _, placement, _ := newBillBoundaryFixture(t)
	f.lookupReply = &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: boundaryContextFor(placement.Snapshot)}}}
	if out, err := bb.ObserveBill(context.Background(), placement, placement.Snapshot); err == nil || out.Observation.Effect != domain.EffectUnknown {
		t.Fatal("missing admission accepted", err)
	}
	if f.observes != 0 {
		t.Fatal("observed without admitted receipt")
	}
	bb, f, _, placement, bill := newBillBoundaryFixture(t)
	admission := &r.Receipt{Attempt: bb.Attempt(placement), AdmittedContext: boundaryContextFor(placement.Snapshot), Outcome: &r.Receipt_Uncertain{Uncertain: &r.Uncertain{}}}
	f.lookupReply = &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: admission}}
	badEvidence := proto.Clone(billEffectEvidence(bill)).(*r.EffectEvidence)
	badEvidence.GetBill().RecipeDef = proto.String("ForeignRecipe")
	f.observeReply = &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: &r.Progress{Attempt: bb.Attempt(placement), Context: boundaryContextFor(placement.Snapshot), CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: badEvidence}}}}}
	if _, err := bb.ObserveBill(context.Background(), placement, placement.Snapshot); !errors.Is(err, executor.ErrEvidence) {
		t.Fatal("mismatched recipe evidence accepted", err)
	}
}

func boundaryContextFor(s domain.GenerationSnapshot) *c.ObservationContext {
	return &c.ObservationContext{Identity: boundary.Identity(s), Tick: proto.Int64(int64(10)), NativeGeneration: proto.Uint64(uint64(s.Native))}
}
