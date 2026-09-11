package buildingruntime

import (
	"context"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestPendingConstructionAccountingRequiresExactCompleteNativeEvidence(t *testing.T) {
	for _, fault := range []string{"", "partial", "building", "absent", "failed", "identity", "origin"} {
		t.Run(fault, func(t *testing.T) {
			b, f := newBoundaryFixture(t)
			effect := f.progress.GetCompleted().Evidence
			effect.GetConstruction().Stage = r.ConstructionStage_CONSTRUCTION_STAGE_FRAME.Enum()
			f.receipt.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: proto.Clone(effect).(*r.EffectEvidence)}}
			f.progress.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: effect}}
			switch fault {
			case "partial":
				f.progress.CompleteInspection = proto.Bool(false)
			case "building":
				effect.GetConstruction().Stage = r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING.Enum()
			case "absent":
				effect.GetConstruction().Present = proto.Bool(false)
			case "failed":
				effect.GetConstruction().Failed = proto.Bool(true)
			case "identity":
				effect.GetConstruction().Cell.X = proto.Int32(99)
			case "origin":
				effect.GetConstruction().OriginThingId = proto.String("other")
			}
			got, err := b.Observe(context.Background(), f.placement, f.placement.Snapshot)
			if fault == "" || fault == "partial" {
				if err != nil || got.Observation.ConstructionObserved != (fault == "") {
					t.Fatal(got, err)
				}
			} else if err == nil {
				t.Fatal("invalid accounting proof accepted")
			}
		})
	}
}
