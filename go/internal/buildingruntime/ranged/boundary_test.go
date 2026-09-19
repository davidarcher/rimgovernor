package ranged

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// rangedFixture serves one rifle-armed attacker and either a hostile pawn
// (two rows) or a census-listed hostile building (the attacker's row
// alone, the building under Threats).
type rangedFixture struct {
	*draft.Fixture
	opponent       *n.PawnState
	command        *o.AttackTarget
	buildingTarget bool
	threats        []policy.EmergencyThreat
	emergencyReads int
}

func newRangedFixture(t *testing.T) (*RangedAttackBoundary, *rangedFixture, executor.RangedDispatch) {
	t.Helper()
	_, f := draft.NewFixture(t)
	claim := draft.KnownClaim(f)
	attack, err := domain.NewRangedAttack("pawn", "target", "action")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRangedAttackAction("attack", attack)
	if err != nil {
		t.Fatal(err)
	}
	f.P.Action = action
	f.Row.Dead = proto.Bool(false)
	f.Row.Downed = proto.Bool(false)
	f.Row.FreeColonist = proto.Bool(true)
	f.Row.Health = &n.PawnHealth{SummaryFraction: proto.Float64(1), Bleeding: proto.Bool(false), NeedsTend: proto.Bool(false)}
	f.Row.Biography = &n.PawnBiography{}
	f.Row.Equipment = &n.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("rifle"), Equipped: []*n.GearItem{{Thing: &n.EntityRef{Id: proto.String("rifle")}, Ranged: proto.Bool(true), Range: proto.Float64(37)}}}
	opponent := proto.Clone(f.Row).(*n.PawnState)
	opponent.Pawn.Id = proto.String("target")
	opponent.Pawn.Snapshot.EntityId = proto.String("target")
	opponent.Pawn.Snapshot.Token = proto.String("target-cas")
	opponent.Hostile = proto.Bool(true)
	f.Receipt.Attempt.ActionId = proto.String("attack")
	f.Progress.Attempt.ActionId = proto.String("attack")
	for _, job := range []*r.JobEffect{boundary.ReceiptJob(f.Receipt), f.Progress.GetCompleted().Evidence.GetJob()} {
		job.JobId = proto.Int32(42)
		job.JobDef = proto.String("AttackStatic")
		job.TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "target"}}
	}
	fixture := &rangedFixture{Fixture: f, opponent: opponent}
	b, err := NewRangedBoundary(fixture, fixture, fixture, boundary.FixedClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	d := executor.RangedDispatch{Attempt: f.P, Admission: store.MeleeAdmission{Snapshot: f.P.Snapshot, Tick: 10, Pawn: "pawn", Target: "target", PawnSnapshotToken: "cas", TargetSnapshotToken: "target-cas", DraftClaim: claim}}
	return b, fixture, d
}
func (f *rangedFixture) ReadCombatPawns(ctx context.Context, id *c.Identity, ids []string) (*n.ListPawnsReply, bridge.Result, error) {
	if !proto.Equal(id, f.Ctx.Identity) {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	rows := []*n.PawnState{proto.Clone(f.Row).(*n.PawnState)}
	if !f.buildingTarget {
		rows = append(rows, proto.Clone(f.opponent).(*n.PawnState))
	}
	return &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: &n.PawnSnapshot{Context: proto.Clone(f.Ctx).(*c.ObservationContext), Pawns: rows, Completeness: &n.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(len(rows))), Returned: proto.Uint64(uint64(len(rows))), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, ctx.Err()
}
func (f *rangedFixture) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	f.emergencyReads++
	out, raw, err := f.Fixture.ReadEmergency(ctx, id)
	out.Facts.Threats = append([]policy.EmergencyThreat(nil), f.threats...)
	return out, raw, err
}
func (f *rangedFixture) PreviewAttack(ctx context.Context, id *c.Identity, command *o.AttackTarget) (*o.PreviewReply, bridge.Result, error) {
	f.command = proto.Clone(command).(*o.AttackTarget)
	current := proto.Clone(f.Ctx).(*c.ObservationContext)
	return &o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: current, Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("AttackStatic"), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "target"}}, CanTry: proto.Bool(true)}}}}}}, bridge.Result{}, ctx.Err()
}
func (f *rangedFixture) AttackTarget(ctx context.Context, pre *a.WritePrecondition, command *o.AttackTarget) (*o.ExecuteReply, bridge.Result, error) {
	f.Writes++
	f.command = proto.Clone(command).(*o.AttackTarget)
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: proto.Clone(f.Receipt).(*r.Receipt)}}, bridge.Result{}, f.WriteErr
}
func (f *rangedFixture) LookupAttackAttempt(ctx context.Context, attempt bridge.AttackAttempt) (*r.LookupReply, bridge.Result, error) {
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: proto.Clone(f.Receipt).(*r.Receipt)}}, bridge.Result{}, f.ReadErr
}
func (f *rangedFixture) ObserveAttackProgress(ctx context.Context, attempt bridge.AttackAttempt, receipt *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: proto.Clone(f.Progress).(*r.Progress)}}, bridge.Result{}, f.ReadErr
}

func TestRangedBoundaryInspectsAPawnTarget(t *testing.T) {
	t.Parallel()
	b, f, d := newRangedFixture(t)
	v, err := b.InspectRanged(context.Background(), executor.Target{Action: d.Attempt.Action, Snapshot: d.Attempt.Snapshot}, d.Admission.DraftClaim)
	if err != nil || f.emergencyReads != 1 || v.Facts.Target.SnapshotToken != "target-cas" || v.Facts.Pawn.RangedWeaponEquipped != domain.Known(true) || v.Facts.Target.Hostile != domain.Known(true) {
		t.Fatal(v, err, f.emergencyReads)
	}
	receipt, err := b.AttackRanged(context.Background(), d)
	if err != nil || receipt.Kind != domain.ReceiptAccepted || !proto.Equal(f.command, rangedCommand("pawn", "target", "cas", "target-cas")) {
		t.Fatal(receipt, err, f.command)
	}
}

// A hostile building target (#327): the pawn read returns the attacker
// alone, the target's token and standing come from the census row, the
// preview and dispatch carry that token, and a building the census no
// longer lists holds.
func TestRangedBoundaryInspectsAHostileBuildingTarget(t *testing.T) {
	t.Parallel()
	b, f, d := newRangedFixture(t)
	f.buildingTarget = true
	f.threats = []policy.EmergencyThreat{{ID: "target", Kind: policy.HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), SnapshotToken: "ship-cas", Definition: "ShipPart", Cells: []domain.Cell{{X: 5, Z: 5}, {X: 6, Z: 5}}}}
	v, err := b.InspectRanged(context.Background(), executor.Target{Action: d.Attempt.Action, Snapshot: d.Attempt.Snapshot}, d.Admission.DraftClaim)
	if err != nil || f.emergencyReads != 2 || v.Facts.Target.SnapshotToken != "ship-cas" || v.Facts.Target.Dead != domain.Known(false) || v.Facts.Target.Downed != domain.Known(false) || v.Facts.Target.Hostile != domain.Known(true) || f.command.Target.GetExpectedSnapshotToken() != "ship-cas" {
		t.Fatal(v, err, f.emergencyReads, f.command)
	}
	d.Admission.TargetSnapshotToken = "ship-cas"
	receipt, err := b.AttackRanged(context.Background(), d)
	if err != nil || receipt.Kind != domain.ReceiptAccepted || !proto.Equal(f.command, rangedCommand("pawn", "target", "cas", "ship-cas")) {
		t.Fatal(receipt, err, f.command)
	}
	f.threats = nil
	if _, err := b.InspectRanged(context.Background(), executor.Target{Action: d.Attempt.Action, Snapshot: d.Attempt.Snapshot}, d.Admission.DraftClaim); !errors.Is(err, executor.ErrHeld) {
		t.Fatal("a building the census no longer lists must hold", err)
	}
}
