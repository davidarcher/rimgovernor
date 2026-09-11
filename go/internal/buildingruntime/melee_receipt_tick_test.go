package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func TestMeleeBoundaryReceiptTickAcrossObservations(t *testing.T) {
	for _, unknown := range []bool{false, true} {
		name := "pending then completed"
		if unknown {
			name = "unknown then Manual interrupted"
		}
		t.Run(name, func(t *testing.T) {
			b, f, d := meleeFixtureBoundary(t)
			originalReceipt := proto.Clone(f.receipt)
			evidence := f.progress.GetCompleted().Evidence
			f.progress.Context.Tick = proto.Int64(11)
			if unknown {
				f.progress.Effect = &r.Progress_Unknown{Unknown: &r.UnknownEffect{Reason: proto.String("not yet correlated")}}
				f.progress.CompleteInspection = proto.Bool(false)
			} else {
				f.progress.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: evidence}}
			}
			first, err := b.ObserveMelee(context.Background(), d, d.Attempt.Snapshot)
			want := domain.EffectPending
			if unknown {
				want = domain.EffectUnknown
			}
			if err != nil || first.Observation.Effect != want {
				t.Fatal(first, err)
			}
			// The executor reconstructs the original attempt with the latest durable tick.
			d.Attempt.Tick = first.Observation.Tick
			current := d.Attempt.Snapshot
			f.progress.Context.Tick = proto.Int64(12)
			f.progress.CompleteInspection = proto.Bool(true)
			want = domain.EffectCompleted
			if unknown {
				current.Native++
				current.Direction++
				f.progress.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
				evidence.GetJob().Drafted = proto.Bool(false)
				evidence.GetJob().DraftOwner = nil
				evidence.GetJob().DraftClaimId = nil
				evidence.GetJob().Verified = proto.Bool(false)
				f.progress.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED.Enum(), Evidence: evidence}}
				want = domain.EffectUnsuccessful
			} else {
				f.progress.Effect = &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: evidence}}
			}
			last, err := b.ObserveMelee(context.Background(), d, current)
			if err != nil || last.Observation.Effect != want || !last.Complete {
				t.Fatal(last, err)
			}
			if unknown && last.Observation.UnsuccessfulReason != domain.NativeInterrupted {
				t.Fatal(last)
			}
			if !proto.Equal(f.receipt, originalReceipt) || f.leases != 0 || f.writes != 0 {
				t.Fatal("reconciliation mutated receipt or acquired/wrote")
			}
			d.Attempt.Tick = last.Observation.Tick
			f.progress.Context.Tick = proto.Int64(11)
			if _, err = b.ObserveMelee(context.Background(), d, current); err == nil {
				t.Fatal("accepted regressing progress")
			}
			f.progress.Context.Tick = proto.Int64(13)
			f.receipt.AdmittedContext.Tick = proto.Int64(9)
			if _, err = b.ObserveMelee(context.Background(), d, current); err == nil {
				t.Fatal("accepted receipt predating original admission")
			}
		})
	}
}
