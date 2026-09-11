package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"reflect"
	"testing"
)

type meleeFixture struct {
	*draftFixtureNative
	opponent      *n.PawnState
	ids           []string
	command       *o.AttackTarget
	previewTick   int64
	progressReads int
	owner         *a.Owner
}

func meleeFixtureBoundary(t *testing.T) (*MeleeBoundary, *meleeFixture, executor.MeleeDispatch) {
	t.Helper()
	_, f := draftBoundaryFixture(t)
	claim := draftKnownClaim(f)
	attack, err := domain.NewMeleeAttack("pawn", "target", "action")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewMeleeAttackAction("attack", attack)
	if err != nil {
		t.Fatal(err)
	}
	f.p.Action = action
	f.row.Dead = proto.Bool(false)
	f.row.Downed = proto.Bool(false)
	f.row.FreeColonist = proto.Bool(true)
	f.row.Health = &n.PawnHealth{SummaryFraction: proto.Float64(1), Bleeding: proto.Bool(false), NeedsTend: proto.Bool(false)}
	f.row.Biography = &n.PawnBiography{}
	f.row.Equipment = &n.PawnEquipment{Armed: proto.Bool(false)}
	opponent := proto.Clone(f.row).(*n.PawnState)
	opponent.Pawn.Id = proto.String("target")
	opponent.Pawn.Snapshot.EntityId = proto.String("target")
	opponent.Pawn.Snapshot.Token = proto.String("target-cas")
	opponent.Hostile = proto.Bool(true)
	f.receipt.Attempt.ActionId = proto.String("attack")
	f.progress.Attempt.ActionId = proto.String("attack")
	for _, job := range []*r.JobEffect{draftReceiptJob(f.receipt), f.progress.GetCompleted().Evidence.GetJob()} {
		job.JobId = proto.Int32(42)
		job.JobDef = proto.String("AttackMelee")
		job.TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "target"}}
	}
	f.progress.GetCompleted().Evidence.GetJob().Issued = proto.Bool(false)
	fixture := &meleeFixture{draftFixtureNative: f, opponent: opponent, previewTick: 10}
	b, err := NewMeleeBoundary(fixture, fixture, fixture, boundaryClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	d := executor.MeleeDispatch{Attempt: f.p, Admission: store.MeleeAdmission{Snapshot: f.p.Snapshot, Tick: 10, Pawn: "pawn", Target: "target", PawnSnapshotToken: "cas", TargetSnapshotToken: "target-cas", DraftClaim: claim}}
	return b, fixture, d
}
func (f *meleeFixture) ReadCombatPawns(ctx context.Context, id *c.Identity, ids []string) (*n.ListPawnsReply, bridge.Result, error) {
	f.ids = append([]string(nil), ids...)
	if !proto.Equal(id, f.context.Identity) {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	return &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: &n.PawnSnapshot{Context: proto.Clone(f.context).(*c.ObservationContext), Pawns: []*n.PawnState{proto.Clone(f.row).(*n.PawnState), proto.Clone(f.opponent).(*n.PawnState)}, Completeness: &n.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, ctx.Err()
}
func (f *meleeFixture) PreviewAttack(ctx context.Context, id *c.Identity, command *o.AttackTarget) (*o.PreviewReply, bridge.Result, error) {
	f.command = proto.Clone(command).(*o.AttackTarget)
	current := proto.Clone(f.context).(*c.ObservationContext)
	current.Tick = proto.Int64(f.previewTick)
	return &o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: current, Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("AttackMelee"), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "target"}}, CanTry: proto.Bool(true)}}}}}}, bridge.Result{}, ctx.Err()
}
func (f *meleeFixture) AttackTarget(ctx context.Context, pre *a.WritePrecondition, owner *a.Owner, command *o.AttackTarget) (*o.ExecuteReply, bridge.Result, error) {
	f.writes++
	f.lastPre = proto.Clone(pre).(*a.WritePrecondition)
	f.owner = proto.Clone(owner).(*a.Owner)
	f.command = proto.Clone(command).(*o.AttackTarget)
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: proto.Clone(f.receipt).(*r.Receipt)}}, bridge.Result{}, f.writeErr
}
func (f *meleeFixture) LookupAttackAttempt(ctx context.Context, attempt bridge.AttackAttempt) (*r.LookupReply, bridge.Result, error) {
	f.lookups++
	if attempt.Attempt.GetActionId() != "attack" || attempt.NativeGeneration != 2 || attempt.Owner.GetPlayerDirection() != 1 {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	if f.receipt == nil {
		return &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: f.context}}}, bridge.Result{}, nil
	}
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: proto.Clone(f.receipt).(*r.Receipt)}}, bridge.Result{}, f.readErr
}
func (f *meleeFixture) ObserveAttackProgress(ctx context.Context, attempt bridge.AttackAttempt, receipt *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	f.progressReads++
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: proto.Clone(f.progress).(*r.Progress)}}, bridge.Result{}, f.readErr
}
func TestMeleeBoundaryInspectAndExactDispatch(t *testing.T) {
	b, f, d := meleeFixtureBoundary(t)
	v, err := b.InspectMelee(context.Background(), executor.Target{Action: d.Attempt.Action, Snapshot: d.Attempt.Snapshot}, d.Admission.DraftClaim)
	if err != nil || !reflect.DeepEqual(f.ids, []string{"pawn", "target"}) || v.Facts.Pawn.SnapshotToken != "cas" || v.Facts.Target.SnapshotToken != "target-cas" || v.Facts.Pawn.HealthFraction != domain.Known(float64(1)) || v.Facts.Pawn.ViolenceCapable != domain.Known(true) || v.Facts.Pawn.EquipmentKnown != domain.Known(true) {
		t.Fatal(v, err, f.ids)
	}
	owner, known := v.Facts.Pawn.Owner.Value()
	if !known || owner.Claim != "claim" || owner.Session != "session" || owner.Direction != 1 {
		t.Fatal(owner, known)
	}
	receipt, err := b.AttackMelee(context.Background(), d)
	if err != nil || receipt.Kind != domain.ReceiptAccepted || f.leases != 1 || f.writes != 1 || f.lastPre.GetLeaseId() != "lease" || f.owner.GetPlayerDirection() != 1 || !proto.Equal(f.command, meleeCommand("pawn", "target", "cas", "target-cas")) {
		t.Fatal(receipt, err, f.command)
	}
}
func TestMeleeBoundaryRejectsNegativeAdmissionTick(t *testing.T) {
	b, f, d := meleeFixtureBoundary(t)
	d.Admission.Tick = -1
	if _, err := b.AttackMelee(context.Background(), d); err == nil || f.leases != 0 || f.writes != 0 {
		t.Fatal("invalid admission reached native dispatch", err, f.leases, f.writes)
	}
}

func TestMeleeBoundaryInspectMissingAndChangedFacts(t *testing.T) {
	for _, kind := range []string{"generation", "pawn CAS", "target CAS", "preview past", "emergency past", "claim direction", "violent", "missing health", "missing equipment"} {
		t.Run(kind, func(t *testing.T) {
			b, f, d := meleeFixtureBoundary(t)
			reject := false
			switch kind {
			case "generation":
				f.context.NativeGeneration = proto.Uint64(9)
				reject = true
			case "pawn CAS":
				f.row.Pawn.Snapshot.Token = nil
				reject = true
			case "target CAS":
				f.opponent.Pawn.Snapshot.Token = nil
				reject = true
			case "preview past":
				f.previewTick = 9
				reject = true
			case "emergency past":
				f.previewTick = 11
				reject = true
			case "claim direction":
				f.row.DraftClaim.GetOwned().Owner.PlayerDirection = proto.Uint64(9)
			case "violent":
				f.row.Biography.DisabledWorkTags = []string{"Violent"}
			case "missing health":
				f.row.Health = nil
			case "missing equipment":
				f.row.Equipment = nil
			}
			v, err := b.InspectMelee(context.Background(), executor.Target{Action: d.Attempt.Action, Snapshot: d.Attempt.Snapshot}, d.Admission.DraftClaim)
			if reject {
				if err == nil {
					t.Fatal("accepted changed facts", v)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			switch kind {
			case "claim direction":
				owner, known := v.Facts.Pawn.Owner.Value()
				if !known || owner.Direction != 9 {
					t.Fatal("invented original owner", owner)
				}
			case "violent":
				if v.Facts.Pawn.ViolenceCapable != domain.Known(false) {
					t.Fatal(v)
				}
			case "missing health":
				if _, known := v.Facts.Pawn.HealthFraction.Value(); known {
					t.Fatal(v)
				}
			case "missing equipment":
				if _, known := v.Facts.Pawn.EquipmentKnown.Value(); known {
					t.Fatal(v)
				}
			}
		})
	}
}
func TestMeleeBoundaryWriteRefusalAndUncertainty(t *testing.T) {
	for _, code := range []c.FailureCode{c.FailureCode_FAILURE_CODE_INVALID_REQUEST, c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT} {
		b, f, d := meleeFixtureBoundary(t)
		f.writeErr = &bridge.NativeFailure{Value: &c.Failure{Code: code.Enum()}}
		got, err := b.AttackMelee(context.Background(), d)
		if code == c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
			if err == nil || got.Kind != domain.ReceiptUnknown {
				t.Fatal(got, err)
			}
		} else if err != nil || got.Kind != domain.ReceiptRefused {
			t.Fatal(got, err)
		}
	}
	b, f, d := meleeFixtureBoundary(t)
	f.writeErr = context.DeadlineExceeded
	if got, err := b.AttackMelee(context.Background(), d); !errors.Is(err, context.DeadlineExceeded) || got.Kind != domain.ReceiptUnknown {
		t.Fatal(got, err)
	}
}
func TestMeleeBoundaryRecoveryAfterManualAndUnknownReceipt(t *testing.T) {
	for _, uncertain := range []bool{false, true} {
		b, f, d := meleeFixtureBoundary(t)
		if uncertain {
			f.receipt.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{Detail: proto.String("lost readback")}}
		}
		current := d.Attempt.Snapshot
		current.Native = 9
		current.Direction = 2
		f.progress.Context.NativeGeneration = proto.Uint64(9)
		job := f.progress.GetCompleted().Evidence.GetJob()
		job.Drafted = proto.Bool(false)
		job.DraftOwner = nil
		job.DraftClaimId = nil
		got, err := b.ObserveMelee(context.Background(), d, current)
		if err != nil || got.Observation.Effect != domain.EffectCompleted || !got.Complete || f.leases != 0 || f.writes != 0 || f.lookups != 1 {
			t.Fatal(got, err)
		}
	}
}
func TestMeleeBoundaryRejectsUncorrelatedRecovery(t *testing.T) {
	for _, kind := range []string{"claim", "owner direction", "job", "target", "unknown lookup"} {
		t.Run(kind, func(t *testing.T) {
			b, f, d := meleeFixtureBoundary(t)
			switch kind {
			case "claim":
				draftReceiptJob(f.receipt).DraftClaimId = proto.String("other")
			case "owner direction":
				f.receipt.AuthorizingOwner.PlayerDirection = proto.Uint64(3)
			case "job":
				f.progress.GetCompleted().Evidence.GetJob().JobId = proto.Int32(99)
			case "target":
				f.progress.GetCompleted().Evidence.GetJob().TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "other"}}
			case "unknown lookup":
				f.receipt = nil
			}
			if _, err := b.ObserveMelee(context.Background(), d, d.Attempt.Snapshot); err == nil {
				t.Fatal("accepted uncorrelated evidence")
			}
			if f.leases != 0 || f.writes != 0 {
				t.Fatal("recovery acquired permission")
			}
		})
	}
}
func TestMeleeBoundaryTargetDeadIsUnsuccessful(t *testing.T) {
	b, f, d := meleeFixtureBoundary(t)
	evidence := f.progress.GetCompleted().Evidence
	evidence.GetJob().Verified = proto.Bool(false)
	f.progress.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_TARGET_DEAD.Enum(), Evidence: evidence}}
	got, err := b.ObserveMelee(context.Background(), d, d.Attempt.Snapshot)
	if err != nil || got.Observation.Effect != domain.EffectUnsuccessful || got.Observation.UnsuccessfulReason != domain.TargetDead || f.leases != 0 {
		t.Fatal(got, err)
	}
}
