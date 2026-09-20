package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// drillBoundaryNative serves the routine colony fixture to the deconstruction
// boundary; the clearance census stays empty, as it is for player buildings.
type drillBoundaryNative struct{ *resourceNative }

func (d *drillBoundaryNative) ReadClearanceTargets(context.Context, *c.Identity) (*n.ClearanceTargetsReply, bridge.Result, error) {
	return &n.ClearanceTargetsReply{Outcome: &n.ClearanceTargetsReply_Observed{Observed: &n.ClearanceTargetsSnapshot{Context: d.reply.GetObserved().Context, Completeness: &n.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(0), Returned: proto.Uint64(0), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, nil
}
func (d *drillBoundaryNative) ReadAncientShrines(context.Context, *c.Identity) (*n.AncientShrinesReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}
func (d *drillBoundaryNative) LookupDeconstruction(context.Context, bridge.DeconstructionAttempt) (*r.LookupReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}
func (d *drillBoundaryNative) ObserveDeconstructionProgress(context.Context, bridge.DeconstructionAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}

type drillBoundaryWriter struct{}

func (drillBoundaryWriter) ApplyDeconstruction(context.Context, *a.WritePrecondition, string) (*o.ExecuteReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}
func (drillBoundaryWriter) ReleaseDeconstructions(context.Context, *a.WritePrecondition) (*o.ExecuteReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}

type drillBoundaryLease struct{}

func (drillBoundaryLease) Lease(domain.GenerationSnapshot) (string, error) { return "lease", nil }

// The dispatch guard for a drill removal (#538) reads the typed drill census:
// the exact depleted drill is eligible; a still-yielding drill or an unknown
// census never dispatches; a moved, redefined or missing drill is absent. The Home clearance census (which excludes player buildings)
// is never consulted.
func TestDeconstructionBoundaryInspectsDrillCensus(t *testing.T) {
	_, _, session, _, sleeping := sleepingFixture(t)
	native := &drillBoundaryNative{&resourceNative{workshopNative: &workshopNative{sleepingNative: sleeping}}}
	boundary, err := NewDeconstructionBoundary(native, drillBoundaryWriter{}, drillBoundaryLease{}, testkit.NewManualClock(time.Now()), "session")
	if err != nil {
		t.Fatal(err)
	}
	snapshot := session.State().Snapshot
	row := func(depleted bool) *n.DeepDrillState {
		d := &n.DeepDrillState{BuildingId: proto.String("Thing_DeepDrill_7"), DefName: proto.String("DeepDrill"), Position: &c.Cell{X: proto.Int32(4), Z: proto.Int32(1)}, Powered: proto.Bool(true), Depleted: proto.Bool(depleted), Designated: proto.Bool(false)}
		if !depleted {
			d.Resource, d.Remaining = proto.String("Steel"), proto.Int64(12)
		}
		return d
	}
	target := func(id, definition string, cell domain.Cell) executor.Target {
		value, err := domain.NewDrillDeconstruction(id, definition, cell)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewDeconstructionAction("removal-0", value)
		if err != nil {
			t.Fatal(err)
		}
		return executor.Target{Action: action, Snapshot: snapshot}
	}
	exact := target("Thing_DeepDrill_7", "DeepDrill", domain.Cell{X: 4, Z: 1})
	for _, tc := range []struct {
		name     string
		census   *n.DeepResourcesSection
		target   executor.Target
		eligible bool
		err      error
	}{
		{"exhausted", observedDrills(row(true)), exact, true, nil},
		{"yielding", observedDrills(row(false)), exact, false, nil},
		{"census unavailable", &n.DeepResourcesSection{Outcome: &n.DeepResourcesSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("x")}}}, exact, false, executor.ErrHeld},
		{"drill gone", observedDrills(), exact, false, executor.ErrDeconstructionAbsent},
		{"drill moved", observedDrills(row(true)), target("Thing_DeepDrill_7", "DeepDrill", domain.Cell{X: 5, Z: 1}), false, executor.ErrDeconstructionAbsent},
		{"drill redefined", observedDrills(row(true)), target("Thing_DeepDrill_7", "Other", domain.Cell{X: 4, Z: 1}), false, executor.ErrDeconstructionAbsent},
	} {
		t.Run(tc.name, func(t *testing.T) {
			sleeping.reply.GetObserved().DeepResources = tc.census
			out, err := boundary.InspectDeconstruction(context.Background(), tc.target)
			if !errors.Is(err, tc.err) {
				t.Fatal(err, out)
			}
			if err == nil && (out.Eligible != tc.eligible || out.Accepted != tc.eligible || out.Current != snapshot || out.Tick != domain.Tick(sleeping.reply.GetObserved().Context.GetTick())) {
				t.Fatal(out)
			}
		})
	}
}

func observedDrills(rows ...*n.DeepDrillState) *n.DeepResourcesSection {
	return &n.DeepResourcesSection{Outcome: &n.DeepResourcesSection_Observed{Observed: &n.DeepResourcesFacts{Drills: rows}}}
}
