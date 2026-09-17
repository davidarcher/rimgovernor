package melee

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"reflect"
	"testing"
)

func TestMeleeBoundaryInspectAndExactDispatch(t *testing.T) {
	t.Parallel()
	b, f, d := NewFixture(t)
	v, err := b.InspectMelee(context.Background(), executor.Target{Action: d.Attempt.Action, Snapshot: d.Attempt.Snapshot}, d.Admission.DraftClaim)
	if err != nil || !reflect.DeepEqual(f.Ids, []string{"pawn", "target"}) || v.Facts.Pawn.SnapshotToken != "cas" || v.Facts.Target.SnapshotToken != "target-cas" || v.Facts.Pawn.HealthFraction != domain.Known(float64(1)) || v.Facts.Pawn.ViolenceCapable != domain.Known(true) || v.Facts.Pawn.EquipmentKnown != domain.Known(true) {
		t.Fatal(v, err, f.Ids)
	}
	owner, known := v.Facts.Pawn.Owner.Value()
	if !known || owner.Claim != "claim" || owner.Session != "session" {
		t.Fatal(owner, known)
	}
	receipt, err := b.AttackMelee(context.Background(), d)
	if err != nil || receipt.Kind != domain.ReceiptAccepted || f.Leases != 1 || f.Writes != 1 || !proto.Equal(f.Command, meleeCommand("pawn", "target", "cas", "target-cas")) {
		t.Fatal(receipt, err, f.Command)
	}
}
func TestMeleeBoundaryRejectsNegativeAdmissionTick(t *testing.T) {
	t.Parallel()
	b, f, d := NewFixture(t)
	d.Admission.Tick = -1
	if _, err := b.AttackMelee(context.Background(), d); err == nil || f.Leases != 0 || f.Writes != 0 {
		t.Fatal("invalid admission reached native dispatch", err, f.Leases, f.Writes)
	}
}

func TestMeleeBoundaryInspectMissingAndChangedFacts(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"generation", "pawn CAS", "target CAS", "preview past", "emergency past", "violent", "missing health", "missing equipment"} {
		t.Run(kind, func(t *testing.T) {
			b, f, d := NewFixture(t)
			reject := false
			switch kind {
			case "generation":
				f.Ctx.NativeGeneration = proto.Uint64(9)
				reject = true
			case "pawn CAS":
				f.Row.Pawn.Snapshot.Token = nil
				reject = true
			case "target CAS":
				f.Opponent.Pawn.Snapshot.Token = nil
				reject = true
			case "preview past":
				f.PreviewTick = 9
				reject = true
			case "emergency past":
				f.PreviewTick = 11
				reject = true
			case "violent":
				f.Row.Biography.DisabledWorkTags = []string{"Violent"}
			case "missing health":
				f.Row.Health = nil
			case "missing equipment":
				f.Row.Equipment = nil
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
	t.Parallel()
	for _, code := range []c.FailureCode{c.FailureCode_FAILURE_CODE_INVALID_REQUEST, c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT} {
		b, f, d := NewFixture(t)
		f.WriteErr = &bridge.NativeFailure{Value: &c.Failure{Code: code.Enum()}}
		got, err := b.AttackMelee(context.Background(), d)
		if code == c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
			if err == nil || got.Kind != domain.ReceiptUnknown {
				t.Fatal(got, err)
			}
		} else if err != nil || got.Kind != domain.ReceiptRefused {
			t.Fatal(got, err)
		}
	}
	b, f, d := NewFixture(t)
	f.WriteErr = context.DeadlineExceeded
	if got, err := b.AttackMelee(context.Background(), d); !errors.Is(err, context.DeadlineExceeded) || got.Kind != domain.ReceiptUnknown {
		t.Fatal(got, err)
	}
}
func TestMeleeBoundaryRecoveryAfterManualAndUnknownReceipt(t *testing.T) {
	t.Parallel()
	for _, uncertain := range []bool{false, true} {
		b, f, d := NewFixture(t)
		if uncertain {
			f.Receipt.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{Detail: proto.String("lost readback")}}
		}
		current := d.Attempt.Snapshot
		current.Native = 9
		f.Progress.Context.NativeGeneration = proto.Uint64(9)
		job := f.Progress.GetCompleted().Evidence.GetJob()
		job.Drafted = proto.Bool(false)
		job.DraftClaimId = nil
		got, err := b.ObserveMelee(context.Background(), d, current)
		if err != nil || got.Observation.Effect != domain.EffectCompleted || !got.Complete || f.Leases != 0 || f.Writes != 0 || f.Lookups != 1 {
			t.Fatal(got, err)
		}
	}
}
func TestMeleeBoundaryRejectsUncorrelatedRecovery(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"claim", "owner direction", "job", "target", "unknown lookup"} {
		t.Run(kind, func(t *testing.T) {
			b, f, d := NewFixture(t)
			switch kind {
			case "claim":
				boundary.ReceiptJob(f.Receipt).DraftClaimId = proto.String("other")
			case "owner direction":
				f.Receipt.Attempt.AttemptId = proto.Uint64(999)
			case "job":
				f.Progress.GetCompleted().Evidence.GetJob().JobId = proto.Int32(99)
			case "target":
				f.Progress.GetCompleted().Evidence.GetJob().TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "other"}}
			case "unknown lookup":
				f.Receipt = nil
			}
			if _, err := b.ObserveMelee(context.Background(), d, d.Attempt.Snapshot); err == nil {
				t.Fatal("accepted uncorrelated evidence")
			}
			if f.Leases != 0 || f.Writes != 0 {
				t.Fatal("recovery acquired permission")
			}
		})
	}
}
func TestMeleeBoundaryTargetDeadIsUnsuccessful(t *testing.T) {
	t.Parallel()
	b, f, d := NewFixture(t)
	evidence := f.Progress.GetCompleted().Evidence
	evidence.GetJob().Verified = proto.Bool(false)
	f.Progress.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_TARGET_DEAD.Enum(), Evidence: evidence}}
	got, err := b.ObserveMelee(context.Background(), d, d.Attempt.Snapshot)
	if err != nil || got.Observation.Effect != domain.EffectUnsuccessful || got.Observation.UnsuccessfulReason != domain.TargetDead || f.Leases != 0 {
		t.Fatal(got, err)
	}
}
