package melee

import (
	"context"
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

type Fixture struct {
	*draft.Fixture
	Opponent      *n.PawnState
	Ids           []string
	Command       *o.AttackTarget
	PreviewTick   int64
	ProgressReads int
}

func NewFixture(t *testing.T) (*MeleeBoundary, *Fixture, executor.MeleeDispatch) {
	t.Helper()
	_, f := draft.NewFixture(t)
	claim := draft.KnownClaim(f)
	attack, err := domain.NewMeleeAttack("pawn", "target", "action")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewMeleeAttackAction("attack", attack)
	if err != nil {
		t.Fatal(err)
	}
	f.P.Action = action
	f.Row.Dead = proto.Bool(false)
	f.Row.Downed = proto.Bool(false)
	f.Row.FreeColonist = proto.Bool(true)
	f.Row.Health = &n.PawnHealth{SummaryFraction: proto.Float64(1), Bleeding: proto.Bool(false), NeedsTend: proto.Bool(false)}
	f.Row.Biography = &n.PawnBiography{}
	f.Row.Equipment = &n.PawnEquipment{Armed: proto.Bool(false)}
	opponent := proto.Clone(f.Row).(*n.PawnState)
	opponent.Pawn.Id = proto.String("target")
	opponent.Pawn.Snapshot.EntityId = proto.String("target")
	opponent.Pawn.Snapshot.Token = proto.String("target-cas")
	opponent.Hostile = proto.Bool(true)
	f.Receipt.Attempt.ActionId = proto.String("attack")
	f.Progress.Attempt.ActionId = proto.String("attack")
	for _, job := range []*r.JobEffect{boundary.ReceiptJob(f.Receipt), f.Progress.GetCompleted().Evidence.GetJob()} {
		job.JobId = proto.Int32(42)
		job.JobDef = proto.String("AttackMelee")
		job.TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "target"}}
	}
	f.Progress.GetCompleted().Evidence.GetJob().Issued = proto.Bool(false)
	fixture := &Fixture{Fixture: f, Opponent: opponent, PreviewTick: 10}
	b, err := NewMeleeBoundary(fixture, fixture, fixture, boundary.FixedClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	d := executor.MeleeDispatch{Attempt: f.P, Admission: store.MeleeAdmission{Snapshot: f.P.Snapshot, Tick: 10, Pawn: "pawn", Target: "target", PawnSnapshotToken: "cas", TargetSnapshotToken: "target-cas", DraftClaim: claim}}
	return b, fixture, d
}
func (f *Fixture) ReadCombatPawns(ctx context.Context, id *c.Identity, ids []string) (*n.ListPawnsReply, bridge.Result, error) {
	f.Ids = append([]string(nil), ids...)
	if !proto.Equal(id, f.Ctx.Identity) {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	return &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: &n.PawnSnapshot{Context: proto.Clone(f.Ctx).(*c.ObservationContext), Pawns: []*n.PawnState{proto.Clone(f.Row).(*n.PawnState), proto.Clone(f.Opponent).(*n.PawnState)}, Completeness: &n.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, ctx.Err()
}
func (f *Fixture) PreviewAttack(ctx context.Context, id *c.Identity, command *o.AttackTarget) (*o.PreviewReply, bridge.Result, error) {
	f.Command = proto.Clone(command).(*o.AttackTarget)
	current := proto.Clone(f.Ctx).(*c.ObservationContext)
	current.Tick = proto.Int64(f.PreviewTick)
	return &o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: current, Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("AttackMelee"), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "target"}}, CanTry: proto.Bool(true)}}}}}}, bridge.Result{}, ctx.Err()
}
func (f *Fixture) AttackTarget(ctx context.Context, pre *a.WritePrecondition, command *o.AttackTarget) (*o.ExecuteReply, bridge.Result, error) {
	f.Writes++
	f.LastPre = proto.Clone(pre).(*a.WritePrecondition)
	f.Command = proto.Clone(command).(*o.AttackTarget)
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: proto.Clone(f.Receipt).(*r.Receipt)}}, bridge.Result{}, f.WriteErr
}
func (f *Fixture) LookupAttackAttempt(ctx context.Context, attempt bridge.AttackAttempt) (*r.LookupReply, bridge.Result, error) {
	f.Lookups++
	if attempt.Attempt.GetActionId() != "attack" || attempt.NativeGeneration != 2 {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	if f.Receipt == nil {
		return &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: f.Ctx}}}, bridge.Result{}, nil
	}
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: proto.Clone(f.Receipt).(*r.Receipt)}}, bridge.Result{}, f.ReadErr
}
func (f *Fixture) ObserveAttackProgress(ctx context.Context, attempt bridge.AttackAttempt, receipt *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	f.ProgressReads++
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: proto.Clone(f.Progress).(*r.Progress)}}, bridge.Result{}, f.ReadErr
}
