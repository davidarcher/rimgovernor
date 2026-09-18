package movement

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// fixture answers the movement boundary with the draft fixture's pawn row,
// served through the combat read (health detail present).
type fixture struct {
	*draft.Fixture
	combatReads int
}

func (f *fixture) ReadCombatPawns(ctx context.Context, id *c.Identity, ids []string) (*n.ListPawnsReply, bridge.Result, error) {
	f.combatReads++
	return f.Fixture.ReadPawns(ctx, id, ids)
}
func (f *fixture) PreviewMovement(ctx context.Context, _ *c.Identity, command *o.MovePawn) (*o.PreviewReply, bridge.Result, error) {
	job := &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("Goto"), TargetA: &r.JobTarget{Target: &r.JobTarget_Cell{Cell: proto.Clone(command.Destination).(*c.Cell)}}, CanTry: proto.Bool(true)}
	return &o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: proto.Clone(f.Ctx).(*c.ObservationContext), Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: job}}}}}, bridge.Result{}, ctx.Err()
}
func (f *fixture) LookupMovementAttempt(context.Context, bridge.MovementAttempt) (*r.LookupReply, bridge.Result, error) {
	return nil, bridge.Result{}, executor.ErrEvidence
}
func (f *fixture) ObserveMovementProgress(context.Context, bridge.MovementAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return nil, bridge.Result{}, executor.ErrEvidence
}
func (f *fixture) MovePawn(context.Context, *a.WritePrecondition, *o.MovePawn) (*o.ExecuteReply, bridge.Result, error) {
	return nil, bridge.Result{}, executor.ErrEvidence
}

// The movement policy needs the pawn's bleeding and tend facts; only the
// combat pawn read carries them, so the inspection must use it (issue #70:
// every hold-the-line move was refused unknown_facts).
func TestInspectMovementReadsCombatHealth(t *testing.T) {
	_, df := draft.NewFixture(t)
	df.Row.Dead, df.Row.Downed, df.Row.FreeColonist = proto.Bool(false), proto.Bool(false), proto.Bool(true)
	df.Row.Health = &n.PawnHealth{SummaryFraction: proto.Float64(1), Bleeding: proto.Bool(false), NeedsTend: proto.Bool(false)}
	f := &fixture{Fixture: df}
	b, err := NewMovementBoundary(f, f, f, boundary.FixedClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	movement, err := domain.NewMovement("pawn", domain.Cell{X: 3, Z: 4}, "action")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewMovementAction("move", movement)
	if err != nil {
		t.Fatal(err)
	}
	claim := draft.KnownClaim(df)
	inspection, err := b.InspectMovement(context.Background(), executor.Target{Action: action, Snapshot: df.P.Snapshot}, claim)
	if err != nil {
		t.Fatal(err)
	}
	if f.combatReads != 1 {
		t.Fatalf("combat pawn reads = %d", f.combatReads)
	}
	facts := inspection.Facts.Pawn
	for name, fact := range map[string]domain.Fact[bool]{"bleeding": facts.Bleeding, "needsTend": facts.NeedsTend, "dead": facts.Dead, "drafted": facts.Drafted} {
		if _, known := fact.Value(); !known {
			t.Fatalf("%s unknown after combat read", name)
		}
	}
	if _, known := inspection.Facts.NativeCanTry.Value(); !known {
		t.Fatal("native canTry unknown")
	}
}

// noChange answers the write and the ledger with the native no-change
// receipt a move to the pawn's current cell earns: the evidence names the
// destination and the owned claim but, by contract, no job id or def.
type noChange struct {
	*fixture
	receipt *r.Receipt
}

func (f *noChange) MovePawn(context.Context, *a.WritePrecondition, *o.MovePawn) (*o.ExecuteReply, bridge.Result, error) {
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: proto.Clone(f.receipt).(*r.Receipt)}}, bridge.Result{}, nil
}
func (f *noChange) LookupMovementAttempt(context.Context, bridge.MovementAttempt) (*r.LookupReply, bridge.Result, error) {
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: proto.Clone(f.receipt).(*r.Receipt)}}, bridge.Result{}, nil
}
func (f *noChange) ObserveMovementProgress(_ context.Context, attempt bridge.MovementAttempt, _ *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	evidence := proto.Clone(f.receipt.GetNoChange().GetObserved()).(*r.EffectEvidence)
	progress := &r.Progress{Attempt: proto.Clone(attempt.Attempt).(*c.AttemptKey), Context: proto.Clone(f.Ctx).(*c.ObservationContext), CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: evidence}}}
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: progress}}, bridge.Result{}, nil
}

// A hold-the-line move whose pawn already stands on the firing position is
// admitted natively as a no-change receipt with no Goto in its evidence
// (#222: the defenders mustered on the line never dispatched because the
// receipt read as invalid evidence and the action waited forever).
func TestMoveToAcceptsNoChangeReceipt(t *testing.T) {
	_, df := draft.NewFixture(t)
	movement, err := domain.NewMovement("pawn", domain.Cell{X: 3, Z: 4}, "action")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewMovementAction("move", movement)
	if err != nil {
		t.Fatal(err)
	}
	job := &r.JobEffect{PawnId: proto.String("pawn"), TargetA: &r.JobTarget{Target: &r.JobTarget_Cell{Cell: &c.Cell{X: proto.Int32(3), Z: proto.Int32(4)}}}, Issued: proto.Bool(false), Verified: proto.Bool(true), Drafted: proto.Bool(true), DraftClaimId: proto.String("claim"), ResultingSnapshotToken: proto.String("cas")}
	key := &c.AttemptKey{ControllerSessionId: proto.String("session"), ActionId: proto.String("move"), AttemptId: proto.Uint64(1)}
	receipt := &r.Receipt{Attempt: key, AdmittedContext: proto.Clone(df.Ctx).(*c.ObservationContext), Outcome: &r.Receipt_NoChange{NoChange: &r.NoChange{Observed: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: job}}, Detail: proto.String("Pawn already occupies the exact destination.")}}}
	f := &noChange{fixture: &fixture{Fixture: df}, receipt: receipt}
	b, err := NewMovementBoundary(f, f, f, boundary.FixedClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	placement := executor.Placement{Action: action, Snapshot: df.P.Snapshot, Attempt: 1, Tick: 10}
	dispatch := executor.MovementDispatch{Attempt: placement, Admission: store.MovementAdmission{Snapshot: df.P.Snapshot, Tick: 10, Pawn: "pawn", Destination: domain.Cell{X: 3, Z: 4}, PawnSnapshotToken: "cas", DraftClaim: draft.KnownClaim(df)}}
	out, err := b.MoveTo(context.Background(), dispatch)
	if err != nil {
		t.Fatal(err)
	}
	if out.Kind != domain.ReceiptAccepted {
		t.Fatalf("receipt kind = %v", out.Kind)
	}
	evidence, err := b.ObserveMovement(context.Background(), dispatch, df.P.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if !evidence.Complete || evidence.Observation.Effect != domain.EffectCompleted {
		t.Fatalf("observation = %+v", evidence.Observation)
	}
	// An issued Goto that reports no job def is still invalid evidence.
	job.JobDef = proto.String("Goto")
	if _, err := b.MoveTo(context.Background(), dispatch); !errors.Is(err, executor.ErrEvidence) {
		t.Fatalf("no-change receipt with an issued job def: err = %v", err)
	}
}
