package melee

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type subdueFixture struct {
	*Fixture
	command               *o.PawnTargetOrder
	lookups, observations int
}

func (f *subdueFixture) PreviewPawnOrder(ctx context.Context, id *c.Identity, command *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error) {
	f.command = proto.Clone(command).(*o.PawnTargetOrder)
	return f.Fixture.PreviewAttack(ctx, id, meleeCommand("pawn", "target", "cas", "target-cas"))
}
func (f *subdueFixture) Subdue(ctx context.Context, pre *a.WritePrecondition, command *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error) {
	f.command = proto.Clone(command).(*o.PawnTargetOrder)
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *subdueFixture) LookupPawnOrderAttempt(ctx context.Context, attempt bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error) {
	if attempt.Kind != o.PawnOrderKind_PAWN_ORDER_KIND_SUBDUE {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	f.lookups++
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *subdueFixture) ObservePawnOrderProgress(ctx context.Context, attempt bridge.PawnOrderAttempt, receipt *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	if attempt.Kind != o.PawnOrderKind_PAWN_ORDER_KIND_SUBDUE {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	f.observations++
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: f.Progress}}, bridge.Result{}, nil
}
func TestSubdueUsesPawnOrderAndOwnedMeleeReconciliation(t *testing.T) {
	_, base, d := NewFixture(t)
	f := &subdueFixture{Fixture: base}
	m, _ := domain.NewSubdue("pawn", "target", "action")
	d.Attempt.Action, _ = domain.NewMeleeAttackAction("attack", m)
	b, err := NewMeleeBoundary(f, f, f, boundary.FixedClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	_, err = b.InspectMelee(context.Background(), executor.Target{Action: d.Attempt.Action, Snapshot: d.Attempt.Snapshot}, d.Admission.DraftClaim)
	if err != nil {
		t.Fatal(err)
	}
	got, err := b.AttackMelee(context.Background(), d)
	if err != nil || got.Kind != domain.ReceiptAccepted || f.Writes != 0 || f.command.GetKind() != o.PawnOrderKind_PAWN_ORDER_KIND_SUBDUE || f.command.GetRequireSafeStorage() || f.command.Pawn.GetExpectedSnapshotToken() != "cas" || f.command.Target.GetExpectedSnapshotToken() != "target-cas" {
		t.Fatal(got, err, f.command)
	}
	evidence, err := b.ObserveMelee(context.Background(), d, d.Attempt.Snapshot)
	if err != nil || evidence.Observation.Effect != domain.EffectCompleted || f.lookups != 1 || f.observations != 1 || f.Lookups != 0 || f.ProgressReads != 0 {
		t.Fatal(evidence, err)
	}
	f.Progress.GetCompleted().Evidence.GetJob().DraftClaimId = proto.String("someone-else")
	if _, err = b.ObserveMelee(context.Background(), d, d.Attempt.Snapshot); err == nil {
		t.Fatal("accepted another owner's claim")
	}
}
