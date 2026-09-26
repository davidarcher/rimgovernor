package acquisition

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

type inspectionNative struct {
	AcquisitionNative
	read      bridge.AcquisitionRead
	preview   *op.PreviewReply
	emergency bridge.EmergencyObservation
}

func (n *inspectionNative) ReadAcquisition(context.Context, *c.Identity, domain.Acquisition) (bridge.AcquisitionRead, bridge.Result, error) {
	return n.read, bridge.Result{}, nil
}
func (n *inspectionNative) PreviewAcquisition(context.Context, *c.Identity, bridge.AcquisitionTarget) (*op.PreviewReply, bridge.Result, error) {
	return n.preview, bridge.Result{}, nil
}
func (n *inspectionNative) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return n.emergency, bridge.Result{}, nil
}

func TestAcquisitionInspectionAcceptsAPreviewAheadOfItsCensus(t *testing.T) {
	base, f := boundary.NewFixture(t)
	plant, _ := domain.NewAcquisition("healroot", "MedicineHerbal", domain.Cell{X: 125, Z: 128})
	action, _ := domain.NewAcquisitionAction("harvest", plant)
	readContext := proto.Clone(f.Receipt.AdmittedContext).(*c.ObservationContext)
	readContext.Tick = proto.Int64(194969)
	previewContext := proto.Clone(readContext).(*c.ObservationContext)
	previewContext.Tick = proto.Int64(195426)
	n := &inspectionNative{
		read:      bridge.AcquisitionRead{Context: readContext, Targets: []bridge.AcquisitionTarget{{Acquisition: plant, Token: "plant-token"}}},
		preview:   &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: previewContext, Accepted: proto.Bool(true)}}},
		emergency: f.Emergency,
	}
	n.emergency.Context = proto.Clone(previewContext).(*c.ObservationContext)
	b := NewAcquisitionBoundary(base, AcquisitionCapabilities{Native: n})
	parent := bridge.NewFactCache()
	cache := bridge.NewChildReadCache(parent)
	ctx := bridge.WithStepReadCache(context.Background(), cache)
	// A preview hundreds of ticks past its census still dispatches: native
	// revalidates the harvest when it applies. An absent source remains a
	// world-condition hold and does not flush the cache.
	inspection, err := b.InspectAcquisition(ctx, executor.Target{Action: action, Snapshot: f.Placement.Snapshot})
	if err != nil || !inspection.Accepted || inspection.Tick != 195426 || parent.Stats().Invalidations != 0 {
		t.Fatal(inspection, err, parent.Stats())
	}
	n.read.Targets = nil
	if _, err = b.InspectAcquisition(ctx, executor.Target{Action: action, Snapshot: f.Placement.Snapshot}); !errors.Is(err, executor.ErrHeld) || errors.Is(err, executor.ErrAcquisitionStale) || parent.Stats().Invalidations != 0 {
		t.Fatal("target absence is not stale facts", err, parent.Stats())
	}
}
