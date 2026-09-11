package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"path/filepath"
	"testing"
	"time"
)

type boundaryClock struct{}

func (boundaryClock) Now() time.Time { return time.Unix(100, 0) }

type boundaryFixture struct {
	placement                         executor.Placement
	bounds                            bridge.MapBounds
	preview                           bridge.BuildingPreview
	emergency                         bridge.EmergencyObservation
	emergencyErr                      error
	emergencyHook                     func()
	emergencies                       int
	receipt                           *r.Receipt
	progress                          *r.Progress
	leaseErr, holdErr, placeErr       error
	unknown                           bool
	leases, places, lookups, observes int
	lastPre                           *a.WritePrecondition
}

func newBoundaryFixture(t *testing.T) (*Boundary, *boundaryFixture) {
	t.Helper()
	building, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 2}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewBuildingAction("action", building)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: "plan", Revision: 1, Native: 1}
	f := &boundaryFixture{placement: executor.Placement{Action: action, Snapshot: snapshot, Attempt: 1, Tick: 10}}
	ctx := &c.ObservationContext{Identity: boundaryIdentity(snapshot), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}
	f.bounds = bridge.MapBounds{Context: proto.Clone(ctx).(*c.ObservationContext), Bounds: policy.Bounds{Width: 100, Height: 100}}
	f.preview = bridge.BuildingPreview{Preview: policy.Preview{Action: action, Snapshot: snapshot, Tick: 11}, Stock: policy.StockObservation{Snapshot: snapshot, Tick: 11}}
	f.emergency = bridge.EmergencyObservation{Context: proto.Clone(ctx).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}}}
	f.emergency.Context.Tick = proto.Int64(12)
	attempt := &c.AttemptKey{ControllerSessionId: proto.String("session"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}
	f.receipt = &r.Receipt{Attempt: attempt, AdmittedContext: ctx, AuthorizingOwner: &a.Owner{ControllerSessionId: proto.String("session"), PlayerDirection: proto.Uint64(1)}, Outcome: &r.Receipt_Uncertain{Uncertain: &r.Uncertain{}}}
	effect := &r.ConstructionEffect{OriginThingId: proto.String("blueprint1"), CurrentThingId: proto.String("building1"), DefName: proto.String("Wall"), Stuff: proto.String("WoodLog"), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Rotation: p.Rotation_ROTATION_NORTH.Enum(), Stage: r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING.Enum(), Present: proto.Bool(true), Started: proto.Bool(true), Failed: proto.Bool(false)}
	f.progress = &r.Progress{Attempt: proto.Clone(attempt).(*c.AttemptKey), Context: proto.Clone(ctx).(*c.ObservationContext), CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: effect}}}}}
	boundary, err := NewBoundary(f, f, f, f, boundaryClock{}, "session", nil)
	if err != nil {
		t.Fatal(err)
	}
	return boundary, f
}
func (f *boundaryFixture) PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	return f.preview, bridge.Result{}, nil
}
func (f *boundaryFixture) ReadEmergency(_ context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	f.emergencies++
	if !proto.Equal(identity, boundaryIdentity(f.placement.Snapshot)) {
		return bridge.EmergencyObservation{}, bridge.Result{}, executor.ErrEvidence
	}
	if f.emergencyHook != nil {
		f.emergencyHook()
	}
	return f.emergency, bridge.Result{}, f.emergencyErr
}
func (f *boundaryFixture) ReadMapBounds(context.Context, *c.Identity, domain.Cell) (bridge.MapBounds, bridge.Result, error) {
	return f.bounds, bridge.Result{}, nil
}
func (f *boundaryFixture) LookupBuildingAttempt(_ context.Context, identity *c.Identity, attempt *c.AttemptKey, generation uint64, _ *p.PlacementCandidate) (*r.LookupReply, bridge.Result, error) {
	f.lookups++
	if !proto.Equal(identity, f.receipt.AdmittedContext.Identity) || !proto.Equal(attempt, f.receipt.Attempt) || generation != 1 {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	if f.unknown {
		return &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: f.progress.Context}}}, bridge.Result{}, nil
	}
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.receipt}}, bridge.Result{}, nil
}
func (f *boundaryFixture) ObserveBuildingProgress(context.Context, *r.Receipt, *p.PlacementCandidate) (*r.ProgressReply, bridge.Result, error) {
	f.observes++
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: f.progress}}, bridge.Result{}, nil
}
func (f *boundaryFixture) PlaceBuilding(_ context.Context, pre *a.WritePrecondition, _ *p.PlacementCandidate) (*o.ExecuteReply, bridge.Result, error) {
	f.places++
	f.lastPre = pre
	if f.placeErr != nil {
		return nil, bridge.Result{}, f.placeErr
	}
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: f.receipt}}, bridge.Result{}, nil
}
func (f *boundaryFixture) Lease(domain.GenerationSnapshot) (string, error) {
	f.leases++
	return "lease", f.leaseErr
}
func (f *boundaryFixture) Holds(context.Context, domain.GenerationSnapshot) ([]policy.Reservation, error) {
	return nil, f.holdErr
}
func TestBoundaryInspectionNativeContextAndCompleteHolds(t *testing.T) {
	b, f := newBoundaryFixture(t)
	out, err := b.Inspect(context.Background(), executor.Target{Action: f.placement.Action, Snapshot: f.placement.Snapshot})
	if err != nil || !out.ExternalHoldsComplete || out.Tick != 11 {
		t.Fatal(err)
	}
	if !policy.EvaluateEmergency(out.Emergency, out.Current, out.Tick).Clear || f.emergencies != 1 {
		t.Fatal("newer native emergency tick did not bind to preview")
	}
	for _, change := range []func(*boundaryFixture){func(f *boundaryFixture) { f.bounds.Context.Identity.LoadToken = proto.String("other") }, func(f *boundaryFixture) { f.bounds.Context.NativeGeneration = nil }, func(f *boundaryFixture) { f.bounds.Context.Tick = proto.Int64(12) }, func(f *boundaryFixture) { f.preview.Stock.Tick = 12 }, func(f *boundaryFixture) { f.holdErr = errors.New("incomplete catalog") }} {
		b, f := newBoundaryFixture(t)
		change(f)
		if out, err := b.Inspect(context.Background(), executor.Target{Action: f.placement.Action, Snapshot: f.placement.Snapshot}); err == nil || out.ExternalHoldsComplete {
			t.Fatal("unsafe inspection marked complete")
		}
	}
}

func TestBoundaryEmergencyContextRefusals(t *testing.T) {
	for name, change := range map[string]func(*boundaryFixture){
		"colony":             func(f *boundaryFixture) { f.emergency.Context.Identity.ColonyId = proto.String("other") },
		"map":                func(f *boundaryFixture) { f.emergency.Context.Identity.MapId = proto.Int32(2) },
		"load":               func(f *boundaryFixture) { f.emergency.Context.Identity.LoadToken = proto.String("other") },
		"missing generation": func(f *boundaryFixture) { f.emergency.Context.NativeGeneration = nil },
		"changed generation": func(f *boundaryFixture) { f.emergency.Context.NativeGeneration = proto.Uint64(2) },
		"missing tick":       func(f *boundaryFixture) { f.emergency.Context.Tick = nil },
		"regressed tick":     func(f *boundaryFixture) { f.emergency.Context.Tick = proto.Int64(10) },
		"missing context":    func(f *boundaryFixture) { f.emergency.Context = nil },
		"unavailable":        func(f *boundaryFixture) { f.emergencyErr = bridge.ErrUnavailable },
		"malformed facts":    func(f *boundaryFixture) { f.emergency.Facts.Colonists[0].ID = "" },
	} {
		t.Run(name, func(t *testing.T) {
			b, f := newBoundaryFixture(t)
			change(f)
			out, err := b.Inspect(context.Background(), executor.Target{Action: f.placement.Action, Snapshot: f.placement.Snapshot})
			if err == nil || out.ExternalHoldsComplete || f.places != 0 {
				t.Fatal("invalid emergency context authorized inspection", err)
			}
		})
	}
}

type boundaryAdvancingClock struct{ now time.Time }

func (c *boundaryAdvancingClock) Now() time.Time { return c.now }
func TestBoundaryEmergencyGatesBothAdmissionsWithoutWrites(t *testing.T) {
	for _, mode := range []string{"danger", "missing health", "unknown census", "second read danger", "delayed read"} {
		t.Run(mode, func(t *testing.T) {
			ctx := context.Background()
			b, f := newBoundaryFixture(t)
			clock := &boundaryAdvancingClock{now: time.Unix(100, 0)}
			b.clock = clock
			db, err := store.Open(ctx, filepath.Join(t.TempDir(), "state.sqlite"))
			if err != nil {
				t.Fatal(err)
			}
			defer db.Close()
			plan, err := domain.NewPlan("plan", 1, []domain.Action{f.placement.Action})
			if err != nil {
				t.Fatal(err)
			}
			if err = db.CreatePlan(ctx, plan); err != nil {
				t.Fatal(err)
			}
			f.preview.Preview.CanPlace = domain.Known(true)
			f.preview.Preview.SafeToPlace = domain.Known(true)
			f.preview.Preview.MadeFromStuff = domain.Known(false)
			f.preview.Preview.Costs = domain.Known([]policy.Amount{})
			f.preview.Preview.Footprint = domain.Known([]domain.Cell{{X: 1, Z: 2}})
			danger := func() {
				f.emergency.Facts.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
			}
			switch mode {
			case "danger":
				danger()
			case "missing health":
				f.emergency.Facts.Colonists[0].NeedsTend = domain.Unknown[bool]()
			case "unknown census":
				f.emergency.Facts.ThreatsComplete = domain.Unknown[bool]()
			case "second read danger":
				f.emergencyHook = func() {
					if f.emergencies == 2 {
						danger()
					}
				}
			case "delayed read":
				f.emergencyHook = func() { clock.now = clock.now.Add(2 * time.Second) }
			}
			e, err := executor.New(db, b, clock, executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
			if err != nil {
				t.Fatal(err)
			}
			defer e.Stop(ctx)
			if err = e.UpdateAuthority(executor.Authority{Snapshot: f.placement.Snapshot, Enabled: true}); err != nil {
				t.Fatal(err)
			}
			result, err := e.Run(ctx, "plan", "action")
			if !errors.Is(err, executor.ErrHeld) || result.NativeCalled || f.places != 0 {
				t.Fatal("emergency admitted write", result, err)
			}
			state, err := db.LoadPlan(ctx, "plan")
			if err != nil {
				t.Fatal(err)
			}
			if mode == "second read danger" {
				if f.emergencies != 2 || len(state.Admissions) != 1 {
					t.Fatal("prepared reservation was not retained", f.emergencies, state)
				}
			} else if len(state.Admissions) != 0 {
				t.Fatal("held work was prepared")
			}
		})
	}
}
func TestBoundaryPlacementLeaseAndFailureKinds(t *testing.T) {
	b, f := newBoundaryFixture(t)
	out, err := b.Place(context.Background(), f.placement)
	if err != nil || out.Kind != domain.ReceiptUnknown || f.places != 1 || f.lastPre.GetLeaseId() != "lease" {
		t.Fatal(err)
	}
	b, f = newBoundaryFixture(t)
	f.leaseErr = executor.ErrAuthority
	if out, err = b.Place(context.Background(), f.placement); err == nil || out.Kind != domain.ReceiptUnknown || f.places != 0 {
		t.Fatal("lease error dispatched")
	}
	b, f = newBoundaryFixture(t)
	f.placeErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}
	if out, err = b.Place(context.Background(), f.placement); err != nil || out.Kind != domain.ReceiptRefused {
		t.Fatal(err)
	}
	b, f = newBoundaryFixture(t)
	f.placeErr = &bridge.Refusal{}
	if out, err = b.Place(context.Background(), f.placement); err == nil || out.Kind != domain.ReceiptUnknown {
		t.Fatal("SDK refusal inferred no effect")
	}
	b, f = newBoundaryFixture(t)
	f.receipt.AuthorizingOwner.PlayerDirection = proto.Uint64(2)
	if out, err = b.Place(context.Background(), f.placement); err == nil || out.Kind != domain.ReceiptUnknown {
		t.Fatal("wrong direction accepted")
	}
	b, f = newBoundaryFixture(t)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err = b.Place(ctx, f.placement); !errors.Is(err, context.Canceled) || f.places != 0 {
		t.Fatal("cancelled placement dispatched")
	}
}
func TestBoundaryRestartReadsWithoutLeaseAndChecksCompletion(t *testing.T) {
	b, f := newBoundaryFixture(t)
	f.leaseErr = executor.ErrAuthority
	current := f.placement.Snapshot
	current.Direction = 2
	current.Native = 2
	f.progress.Context.NativeGeneration = proto.Uint64(2)
	out, err := b.Observe(context.Background(), f.placement, current)
	if err != nil || out.Observation.Effect != domain.EffectCompleted || !out.Complete || out.Observation.Causality != domain.AfterDispatch || f.leases != 0 || f.places != 0 || f.lookups != 1 || f.observes != 1 {
		t.Fatal("restart reconciliation failed", err)
	}
	b, f = newBoundaryFixture(t)
	f.unknown = true
	out, err = b.Observe(context.Background(), f.placement, f.placement.Snapshot)
	if err == nil || out.Observation.Effect != domain.EffectUnknown || f.places != 0 || f.observes != 0 {
		t.Fatal("unknown lookup retried/inferredabsence")
	}
	for _, change := range []func(*boundaryFixture){func(f *boundaryFixture) { f.progress.CompleteInspection = nil }, func(f *boundaryFixture) { f.progress.Context.Tick = proto.Int64(9) }, func(f *boundaryFixture) { f.progress.Context.NativeGeneration = nil }, func(f *boundaryFixture) { f.progress.Attempt.AttemptId = proto.Uint64(2) }, func(f *boundaryFixture) {
		f.progress.GetCompleted().Evidence.GetConstruction().DefName = proto.String("Door")
	}, func(f *boundaryFixture) { f.receipt.AuthorizingOwner.PlayerDirection = proto.Uint64(2) }} {
		b, f := newBoundaryFixture(t)
		change(f)
		if _, err := b.Observe(context.Background(), f.placement, f.placement.Snapshot); err == nil {
			t.Fatal("unsafe completion accepted")
		}
	}
}

func TestRepeatedPendingAndRestartKeepImmutableAdmissionTick(t *testing.T) {
	b, f := newBoundaryFixture(t)
	completed := proto.Clone(f.progress).(*r.Progress)
	f.progress.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: completed.GetCompleted().Evidence}}
	f.progress.Context.Tick = proto.Int64(11)
	first, err := b.Observe(context.Background(), f.placement, f.placement.Snapshot)
	if err != nil || first.Observation.Effect != domain.EffectPending {
		t.Fatal(err)
	}
	f.placement.Tick = first.Observation.Tick
	f.progress.Context.Tick = proto.Int64(12)
	second, err := b.Observe(context.Background(), f.placement, f.placement.Snapshot)
	if err != nil || second.Observation.Effect != domain.EffectPending {
		t.Fatal("second pending lost admission", err)
	}
	// Recreate the boundary, as after restart, with only the journal's latest tick.
	f.placement.Tick = second.Observation.Tick
	b, err = NewBoundary(f, f, f, f, boundaryClock{}, "session", nil)
	if err != nil {
		t.Fatal(err)
	}
	f.progress = completed
	f.progress.Context.Tick = proto.Int64(13)
	final, err := b.Observe(context.Background(), f.placement, f.placement.Snapshot)
	if err != nil || final.Observation.Effect != domain.EffectCompleted || f.receipt.AdmittedContext.GetTick() != 10 {
		t.Fatal("restart completion lost immutable receipt", err)
	}
	f.progress.Context.Tick = proto.Int64(11)
	if _, err = b.Observe(context.Background(), f.placement, f.placement.Snapshot); err == nil {
		t.Fatal("regressing observation accepted")
	}
	// Initial dispatch still cannot accept an admission predating its inspection.
	f.placement.Tick = 11
	if _, err = b.Place(context.Background(), f.placement); err == nil {
		t.Fatal("old admission accepted for initial placement")
	}
}

func TestAttemptConflictDoesNotProveNoEffect(t *testing.T) {
	b, f := newBoundaryFixture(t)
	f.placeErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT.Enum()}}
	out, err := b.Place(context.Background(), f.placement)
	if err == nil || out.Kind != domain.ReceiptUnknown {
		t.Fatal("existing attempt conflict released uncertainty", err)
	}
}
