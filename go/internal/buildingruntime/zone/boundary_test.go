package zone

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

type zoneBoundaryFixture struct {
	read      bridge.ZoneRead
	preview   *op.PreviewReply
	emergency bridge.EmergencyObservation
}

func (f *zoneBoundaryFixture) ReadZoneTarget(context.Context, *c.Identity, domain.ZoneCreate) (bridge.ZoneRead, bridge.Result, error) {
	return f.read, bridge.Result{}, nil
}
func (f *zoneBoundaryFixture) PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error) {
	return f.preview, bridge.Result{}, nil
}
func (f *zoneBoundaryFixture) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return f.emergency, bridge.Result{}, nil
}
func (f *zoneBoundaryFixture) LookupZone(context.Context, bridge.ZoneAttempt) (*r.LookupReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}
func (f *zoneBoundaryFixture) ObserveZone(context.Context, bridge.ZoneAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}
func (f *zoneBoundaryFixture) CreateZone(context.Context, *a.WritePrecondition, bridge.ZoneTarget) (*op.ExecuteReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}

func newZoneBoundaryFixture(t *testing.T) (*ZoneBoundary, *zoneBoundaryFixture, executor.Target) {
	t.Helper()
	base, bf := boundary.NewFixture(t)
	zone, err := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction("action", zone)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := bf.Placement.Snapshot
	ctx := &c.ObservationContext{Identity: boundary.Identity(snapshot), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}
	f := &zoneBoundaryFixture{
		read: bridge.ZoneRead{Context: proto.Clone(ctx).(*c.ObservationContext), Token: "token"},
		preview: &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
			Context: proto.Clone(ctx).(*c.ObservationContext), Accepted: proto.Bool(true),
		}}},
		emergency: bridge.EmergencyObservation{Context: proto.Clone(ctx).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}},
	}
	zb := &ZoneBoundary{Boundary: base, zone: ZoneCapabilities{Native: f, Writer: f}}
	return zb, f, executor.Target{Action: action, Snapshot: snapshot}
}

// The clock may run between the cell read, the preview and the emergency
// read: the inspection accepts ticks in that order (the read's token binds
// the cells) so a zone dispatches mid-window instead of waiting for the
// next pause (#150), and holds only when a read predates the one before it.
func TestInspectZoneAcceptsAdvancingTicksAcrossItsReads(t *testing.T) {
	t.Parallel()
	zb, f, target := newZoneBoundaryFixture(t)
	f.read.Context.Tick = proto.Int64(6)
	f.emergency.Context.Tick = proto.Int64(18)
	out, err := zb.InspectZone(context.Background(), target)
	if err != nil || !out.Accepted || out.Tick != 11 || out.SnapshotToken != "token" {
		t.Fatal(err, out)
	}
	f.read.Context.Tick = proto.Int64(12)
	if _, err := zb.InspectZone(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	// An emergency read from the fact cache a bounded advance behind the
	// preview still covers it (#244); one further behind does not.
	f.preview.GetEvaluated().Context.Tick = proto.Int64(1011)
	f.read.Context.Tick = proto.Int64(1011)
	f.emergency.Context.Tick = proto.Int64(1009)
	if out, err := zb.InspectZone(context.Background(), target); err != nil || !out.Accepted || out.Tick != 1011 {
		t.Fatal(err, out)
	}
	f.emergency.Context.Tick = proto.Int64(1011 - int64(domain.PlanningTickTolerance) - 1)
	if _, err := zb.InspectZone(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
}
