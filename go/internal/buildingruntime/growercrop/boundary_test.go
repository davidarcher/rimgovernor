package growercrop

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

type growerFixture struct {
	target    bridge.GrowerCropTarget
	preview   *op.PreviewReply
	emergency bridge.EmergencyObservation
}

func (f *growerFixture) ReadGrowerCropTarget(context.Context, *c.Identity, string) (bridge.GrowerCropTarget, bridge.Result, error) {
	return f.target, bridge.Result{}, nil
}
func (f *growerFixture) PreviewGrowerCrop(context.Context, *c.Identity, domain.GrowerCrop) (*op.PreviewReply, bridge.Result, error) {
	return f.preview, bridge.Result{}, nil
}
func (f *growerFixture) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return f.emergency, bridge.Result{}, nil
}
func (f *growerFixture) LookupGrowerCrop(context.Context, bridge.GrowerCropAttempt) (*r.LookupReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}
func (f *growerFixture) ObserveGrowerCrop(context.Context, bridge.GrowerCropAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}
func (f *growerFixture) ApplyGrowerCrop(context.Context, *a.WritePrecondition, domain.GrowerCrop) (*op.ExecuteReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}

func newGrowerFixture(t *testing.T) (*Boundary, *growerFixture, executor.Target) {
	t.Helper()
	base, bf := boundary.NewFixture(t)
	crop, err := domain.NewGrowerCrop("Thing_HydroponicsBasin1", "Plant_Potato", "crop-before")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewGrowerCropAction("action", crop)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := bf.Placement.Snapshot
	ctx := &c.ObservationContext{Identity: boundary.Identity(snapshot), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}
	f := &growerFixture{
		target: bridge.GrowerCropTarget{Context: proto.Clone(ctx).(*c.ObservationContext), Thing: "Thing_HydroponicsBasin1", Token: "crop-before", Crop: "Plant_Rice"},
		preview: &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
			Context: proto.Clone(ctx).(*c.ObservationContext), Accepted: proto.Bool(true),
		}}},
		emergency: bridge.EmergencyObservation{Context: proto.Clone(ctx).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}},
	}
	return NewBoundary(base, Capabilities{Native: f, Writer: f}), f, executor.Target{Action: action, Snapshot: snapshot}
}

// The clock may run between the grower read, the preview and the emergency
// read: the inspection accepts ticks in that order (the read's token binds
// the crop) so a basin re-crops mid-window instead of holding for the whole
// watch (#195), and holds only when a read predates the one before it.
func TestInspectGrowerCropAcceptsAdvancingTicksAcrossItsReads(t *testing.T) {
	t.Parallel()
	b, f, target := newGrowerFixture(t)
	f.target.Context.Tick = proto.Int64(6)
	f.emergency.Context.Tick = proto.Int64(18)
	out, err := b.InspectGrowerCrop(context.Background(), target)
	if err != nil || !out.Accepted || out.Tick != 11 || out.SnapshotToken != "crop-before" {
		t.Fatal(err, out)
	}
	f.target.Context.Tick = proto.Int64(12)
	if _, err := b.InspectGrowerCrop(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	f.target.Context.Tick = proto.Int64(11)
	f.emergency.Context.Tick = proto.Int64(9)
	if _, err := b.InspectGrowerCrop(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
}

// A grower whose crop moved since the plan was made holds: the token no
// longer matches the one the patch carries.
func TestInspectGrowerCropHoldsOnStaleToken(t *testing.T) {
	t.Parallel()
	b, f, target := newGrowerFixture(t)
	f.target.Token = "crop-after"
	if _, err := b.InspectGrowerCrop(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
}
