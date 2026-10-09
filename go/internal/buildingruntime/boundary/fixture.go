package boundary

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// Fixture is a fake native building dependency shared across the
// buildingruntime session/worker tests and every "direct-write" family
// (Bill, Zone, Work, Supply, Acquisition) that embeds a *Boundary built from
// it, so those tests need not duplicate a building-boundary fake.
type Fixture struct {
	Placement     executor.Placement
	Bounds        bridge.MapBounds
	Preview       bridge.BuildingPreview
	Emergency     bridge.EmergencyObservation
	EmergencyErr  error
	EmergencyHook func()
	Emergencies   int
	// PreviewHook and BoundsHook run inside the preview and bounds reads;
	// Previews counts the preview reads.
	PreviewHook, BoundsHook           func()
	Previews                          int
	Receipt                           *r.Receipt
	Progress                          *r.Progress
	LeaseErr, HoldErr, PlaceErr       error
	Unknown                           bool
	Leases, Places, Lookups, Observes int
	LastPre                           *a.WritePrecondition
	// Refuse, when set, is the reason Apply refuses every intent with;
	// LastKeys are the keys of the last Apply batch.
	Refuse string
	// RefuseClass, when set, is the class the refusal carries.
	RefuseClass c.RefusalClass
	LastKeys    []string
}

func NewFixture(t *testing.T) (*Boundary, *Fixture) {
	t.Helper()
	building, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 2}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewBuildingAction("action", building, domain.TierExpand)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1, Native: 1}
	f := &Fixture{Placement: executor.Placement{Action: action, Snapshot: snapshot, Attempt: 1, Tick: 10}}
	ctx := &c.ObservationContext{Identity: Identity(snapshot), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}
	f.Bounds = bridge.MapBounds{Context: proto.Clone(ctx).(*c.ObservationContext), Bounds: policy.Bounds{Width: 100, Height: 100}}
	f.Preview = bridge.BuildingPreview{Preview: policy.Preview{Action: action, Snapshot: snapshot, Tick: 11}, Stock: policy.StockObservation{Snapshot: snapshot, Tick: 11}}
	f.Emergency = bridge.EmergencyObservation{Context: proto.Clone(ctx).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), InBed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}}}
	f.Emergency.Context.Tick = proto.Int64(12)
	attempt := &c.AttemptKey{ControllerSessionId: proto.String("session"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}
	f.Receipt = &r.Receipt{Attempt: attempt, AdmittedContext: ctx, Outcome: &r.Receipt_Uncertain{Uncertain: &r.Uncertain{}}}
	effect := &r.ConstructionEffect{OriginThingId: proto.String("blueprint1"), CurrentThingId: proto.String("building1"), DefName: proto.String("Wall"), Stuff: proto.String("WoodLog"), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Rotation: p.Rotation_ROTATION_NORTH.Enum(), Stage: r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING.Enum(), Present: proto.Bool(true), Started: proto.Bool(true), Failed: proto.Bool(false)}
	f.Progress = &r.Progress{Attempt: proto.Clone(attempt).(*c.AttemptKey), Context: proto.Clone(ctx).(*c.ObservationContext), CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: effect}}}}}
	boundary, err := NewBoundary(f, f, f, FixedClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	return boundary, f
}
func (f *Fixture) PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	f.Previews++
	if f.PreviewHook != nil {
		f.PreviewHook()
	}
	return f.Preview, bridge.Result{}, nil
}
func (f *Fixture) ReadEmergency(_ context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	f.Emergencies++
	if !proto.Equal(identity, Identity(f.Placement.Snapshot)) {
		return bridge.EmergencyObservation{}, bridge.Result{}, executor.ErrEvidence
	}
	if f.EmergencyHook != nil {
		f.EmergencyHook()
	}
	return f.Emergency, bridge.Result{}, f.EmergencyErr
}
func (f *Fixture) ReadMapBounds(context.Context, *c.Identity, domain.Cell) (bridge.MapBounds, bridge.Result, error) {
	if f.BoundsHook != nil {
		f.BoundsHook()
	}
	return f.Bounds, bridge.Result{}, nil
}

// Apply is the fake Actions/Apply: every building intent applies, unless
// PlaceErr fails the call or Refuse refuses it.
func (f *Fixture) Apply(_ context.Context, identity *c.Identity, actions []*o.Action) (*o.ApplyReply, bridge.Result, error) {
	f.Places++
	f.LastKeys = f.LastKeys[:0]
	if f.PlaceErr != nil {
		return nil, bridge.Result{}, f.PlaceErr
	}
	reply := &o.ApplyReply{}
	for _, action := range actions {
		f.LastKeys = append(f.LastKeys, action.GetKey())
		if f.Refuse != "" {
			reply.Results = append(reply.Results, &o.ActionResult{Key: action.Key, Outcome: &o.ActionResult_Refused{Refused: &o.Refusal{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Reason: proto.String(f.Refuse), RefusalClass: refuseClass(f.RefuseClass)}}})
			continue
		}
		ctx := &c.ObservationContext{Identity: proto.Clone(identity).(*c.Identity), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}
		applied := &r.Receipt{AdmittedContext: ctx, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: &r.EffectEvidence{}}}}
		reply.Results = append(reply.Results, &o.ActionResult{Key: action.Key, Outcome: &o.ActionResult_Applied{Applied: applied}})
	}
	return reply, bridge.Result{}, nil
}
func (f *Fixture) Lease(domain.GenerationSnapshot) (string, error) {
	f.Leases++
	return "lease", f.LeaseErr
}
func (f *Fixture) Holds(context.Context, domain.GenerationSnapshot) ([]policy.Reservation, error) {
	return nil, f.HoldErr
}

func refuseClass(class c.RefusalClass) *c.RefusalClass {
	if class == c.RefusalClass_REFUSAL_CLASS_UNSPECIFIED {
		return nil
	}
	return class.Enum()
}
