package boundary

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func TestBoundaryInspectionNativeContextAndCompleteHolds(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	out, err := b.Inspect(context.Background(), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot})
	if err != nil || !out.ExternalHoldsComplete || out.Tick != 11 {
		t.Fatal(err)
	}
	if !policy.EvaluateEmergency(out.Emergency, out.Current, out.Tick).Clear || f.Emergencies != 1 {
		t.Fatal("newer native emergency tick did not bind to preview")
	}
	for _, change := range []func(*Fixture){func(f *Fixture) { f.Bounds.Context.Identity.LoadToken = proto.String("other") }, func(f *Fixture) { f.Bounds.Context.NativeGeneration = nil }, func(f *Fixture) { f.Bounds.Context.Tick = proto.Int64(12) }, func(f *Fixture) { f.Preview.Stock.Tick = 11 + domain.PlanningTickTolerance + 1 }, func(f *Fixture) { f.Preview.Stock.Tick = 10 }, func(f *Fixture) { f.HoldErr = errors.New("incomplete catalog") }} {
		b, f := NewFixture(t)
		change(f)
		if out, err := b.Inspect(context.Background(), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot}); err == nil || out.ExternalHoldsComplete {
			t.Fatal("unsafe inspection marked complete")
		}
	}
}

// Under a running window the preview runs ticks after the bounds read
// that opened the inspection, and the emergency read comes from the fact
// cache up to the planning tolerance behind that first read (#244): the
// inspection accepts it and binds the facts to the preview tick.
func TestBoundaryAcceptsACachedEmergencyReadBehindTheBoundsRead(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	bounds := f.Bounds.Context.GetTick() + 1000
	f.Bounds.Context.Tick = proto.Int64(bounds)
	f.Preview.Preview.Tick = domain.Tick(bounds + 2*int64(domain.PlanningTickTolerance))
	f.Preview.Stock.Tick = f.Preview.Preview.Tick
	f.Emergency.Context.Tick = proto.Int64(bounds - int64(domain.PlanningTickTolerance))
	out, err := b.Inspect(context.Background(), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot})
	if err != nil || !out.ExternalHoldsComplete || out.Tick != f.Preview.Preview.Tick {
		t.Fatal(err, out.Tick)
	}
}

// A cached emergency row too far behind the bounds read (the clock ran on
// through the step) is read again live rather than refusing the dispatch
// for the rest of the window (#690).
func TestBoundaryRereadsAnEmergencyRowTooFarBehindTheBoundsRead(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	bounds := f.Bounds.Context.GetTick() + 1200
	f.Bounds.Context.Tick = proto.Int64(bounds)
	f.Preview.Preview.Tick = domain.Tick(bounds + 600)
	f.Preview.Stock.Tick = f.Preview.Preview.Tick
	f.Emergency.Context.Tick = proto.Int64(bounds - 1200)
	f.EmergencyHook = func() {
		if f.Emergencies == 2 {
			f.Emergency.Context.Tick = proto.Int64(bounds + 600)
		}
	}
	out, err := b.Inspect(context.Background(), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot})
	if err != nil || !out.ExternalHoldsComplete || f.Emergencies != 2 {
		t.Fatal(err, f.Emergencies)
	}
}

func TestBoundaryEmergencyContextRefusals(t *testing.T) {
	t.Parallel()
	for name, change := range map[string]func(*Fixture){
		"colony":             func(f *Fixture) { f.Emergency.Context.Identity.ColonyId = proto.String("other") },
		"map":                func(f *Fixture) { f.Emergency.Context.Identity.MapId = proto.Int32(2) },
		"load":               func(f *Fixture) { f.Emergency.Context.Identity.LoadToken = proto.String("other") },
		"missing generation": func(f *Fixture) { f.Emergency.Context.NativeGeneration = nil },
		"changed generation": func(f *Fixture) { f.Emergency.Context.NativeGeneration = proto.Uint64(2) },
		"missing tick":       func(f *Fixture) { f.Emergency.Context.Tick = nil },
		"regressed tick": func(f *Fixture) {
			f.Emergency.Context.Tick = proto.Int64(f.Emergency.Context.GetTick() - int64(domain.PlanningTickTolerance) - 1)
		},
		"missing context": func(f *Fixture) { f.Emergency.Context = nil },
		"unavailable":     func(f *Fixture) { f.EmergencyErr = bridge.ErrUnavailable },
		"malformed facts": func(f *Fixture) { f.Emergency.Facts.Colonists[0].ID = "" },
	} {
		t.Run(name, func(t *testing.T) {
			b, f := NewFixture(t)
			change(f)
			out, err := b.Inspect(context.Background(), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot})
			if err == nil || out.ExternalHoldsComplete || f.Places != 0 {
				t.Fatal("invalid emergency context authorized inspection", err)
			}
		})
	}
}

type boundaryAdvancingClock struct{ now time.Time }

func (c *boundaryAdvancingClock) Now() time.Time { return c.now }
func TestBoundaryEmergencyGatesBothAdmissionsWithoutWrites(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"danger", "missing health", "unknown census", "second read danger", "delayed read"} {
		t.Run(mode, func(t *testing.T) {
			ctx := context.Background()
			b, f := NewFixture(t)
			clock := &boundaryAdvancingClock{now: time.Unix(100, 0)}
			b.Clock = clock
			db, err := store.Open(ctx, storetest.Path(t))
			if err != nil {
				t.Fatal(err)
			}
			defer db.Close()
			plan, err := domain.NewPlan("plan", 1, []domain.Action{f.Placement.Action})
			if err != nil {
				t.Fatal(err)
			}
			if err = db.CreatePlan(ctx, plan); err != nil {
				t.Fatal(err)
			}
			f.Preview.Preview.CanPlace = domain.Known(true)
			f.Preview.Preview.SafeToPlace = domain.Known(true)
			f.Preview.Preview.MadeFromStuff = domain.Known(false)
			f.Preview.Preview.Costs = domain.Known([]policy.Amount{})
			f.Preview.Preview.Footprint = domain.Known([]domain.Cell{{X: 1, Z: 2}})
			danger := func() {
				f.Emergency.Facts.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
			}
			switch mode {
			case "danger":
				danger()
			case "missing health":
				f.Emergency.Facts.Colonists[0].NeedsTend = domain.Unknown[bool]()
			case "unknown census":
				f.Emergency.Facts.ThreatsComplete = domain.Unknown[bool]()
			case "second read danger":
				f.EmergencyHook = func() {
					if f.Emergencies == 2 {
						danger()
					}
				}
			case "delayed read":
				f.EmergencyHook = func() { clock.now = clock.now.Add(2 * time.Second) }
			}
			e, err := executor.New(db, b, clock, executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
			if err != nil {
				t.Fatal(err)
			}
			defer e.Stop(ctx)
			if err = e.UpdateAuthority(executor.Authority{Snapshot: f.Placement.Snapshot, Enabled: true}); err != nil {
				t.Fatal(err)
			}
			result, err := e.Run(ctx, "plan", "action")
			if !errors.Is(err, executor.ErrHeld) || result.NativeCalled || f.Places != 0 {
				t.Fatal("emergency admitted write", result, err)
			}
			state, err := db.LoadPlan(ctx, "plan")
			if err != nil {
				t.Fatal(err)
			}
			if mode == "second read danger" {
				if f.Emergencies != 2 || len(state.Admissions) != 1 {
					t.Fatal("prepared reservation was not retained", f.Emergencies, state)
				}
			} else if len(state.Admissions) != 0 {
				t.Fatal("held work was prepared")
			}
		})
	}
}
func TestBoundaryPlacementLeaseAndFailureKinds(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	out, err := b.Place(context.Background(), f.Placement)
	if err != nil || out.Kind != domain.ReceiptUnknown || f.Places != 1 {
		t.Fatal(err)
	}
	b, f = NewFixture(t)
	f.LeaseErr = executor.ErrAuthority
	if out, err = b.Place(context.Background(), f.Placement); err == nil || out.Kind != domain.ReceiptUnknown || f.Places != 0 {
		t.Fatal("lease error dispatched")
	}
	b, f = NewFixture(t)
	f.PlaceErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}
	if out, err = b.Place(context.Background(), f.Placement); err != nil || out.Kind != domain.ReceiptRefused {
		t.Fatal(err)
	}
	b, f = NewFixture(t)
	f.PlaceErr = &bridge.Refusal{}
	if out, err = b.Place(context.Background(), f.Placement); err == nil || out.Kind != domain.ReceiptUnknown {
		t.Fatal("SDK refusal inferred no effect")
	}
	b, f = NewFixture(t)
	f.Receipt.Attempt.AttemptId = proto.Uint64(999)
	if out, err = b.Place(context.Background(), f.Placement); err == nil || out.Kind != domain.ReceiptUnknown {
		t.Fatal("corrupted receipt accepted")
	}
	b, f = NewFixture(t)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err = b.Place(ctx, f.Placement); !errors.Is(err, context.Canceled) || f.Places != 0 {
		t.Fatal("cancelled placement dispatched")
	}
}
func TestBoundaryRestartReadsWithoutLeaseAndChecksCompletion(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	f.LeaseErr = executor.ErrAuthority
	current := f.Placement.Snapshot
	current.Native = 2
	f.Progress.Context.NativeGeneration = proto.Uint64(2)
	out, err := b.Observe(context.Background(), f.Placement, current)
	if err != nil || out.Observation.Effect != domain.EffectCompleted || !out.Complete || out.Observation.Causality != domain.AfterDispatch || f.Leases != 0 || f.Places != 0 || f.Lookups != 1 || f.Observes != 1 {
		t.Fatal("restart reconciliation failed", err)
	}
	nativeIdentity := f.Progress.GetCompleted().Evidence.GetConstruction()
	if out.Observation.Construction == nil || out.Observation.Construction.Origin != nativeIdentity.GetOriginThingId() || out.Observation.Construction.Current != nativeIdentity.GetCurrentThingId() {
		t.Fatal("native completion identities discarded", out.Observation)
	}
	// A lookup the native ledger does not know was never admitted, so it
	// resolves as complete no-effect evidence without a write or progress
	// read (#71); the executor then re-dispatches a fresh attempt.
	b, f = NewFixture(t)
	f.Unknown = true
	out, err = b.Observe(context.Background(), f.Placement, f.Placement.Snapshot)
	if err != nil || out.Observation.Effect != domain.EffectAbsent || !out.Complete || out.Observation.Causality != domain.AfterDispatch || out.Observation.Tick != domain.Tick(f.Progress.Context.GetTick()) || f.Places != 0 || f.Observes != 0 {
		t.Fatal("unknown lookup not resolved as unadmitted", err, out)
	}
	for _, change := range []func(*Fixture){func(f *Fixture) { f.Progress.CompleteInspection = nil }, func(f *Fixture) { f.Progress.Context.Tick = proto.Int64(9) }, func(f *Fixture) { f.Progress.Context.NativeGeneration = nil }, func(f *Fixture) { f.Progress.Attempt.AttemptId = proto.Uint64(2) }, func(f *Fixture) {
		f.Progress.GetCompleted().Evidence.GetConstruction().DefName = proto.String("Door")
	}, func(f *Fixture) { f.Receipt.Attempt.AttemptId = proto.Uint64(999) }} {
		b, f := NewFixture(t)
		change(f)
		if _, err := b.Observe(context.Background(), f.Placement, f.Placement.Snapshot); err == nil {
			t.Fatal("unsafe completion accepted")
		}
	}
}

func TestRepeatedPendingAndRestartKeepImmutableAdmissionTick(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	completed := proto.Clone(f.Progress).(*r.Progress)
	pending := proto.Clone(completed.GetCompleted().Evidence).(*r.EffectEvidence)
	pending.GetConstruction().Stage = r.ConstructionStage_CONSTRUCTION_STAGE_FRAME.Enum()
	f.Progress.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: pending}}
	f.Progress.Context.Tick = proto.Int64(11)
	first, err := b.Observe(context.Background(), f.Placement, f.Placement.Snapshot)
	if err != nil || first.Observation.Effect != domain.EffectPending || !first.Observation.ConstructionObserved {
		t.Fatal(err)
	}
	f.Placement.Tick = first.Observation.Tick
	f.Progress.Context.Tick = proto.Int64(12)
	second, err := b.Observe(context.Background(), f.Placement, f.Placement.Snapshot)
	if err != nil || second.Observation.Effect != domain.EffectPending {
		t.Fatal("second pending lost admission", err)
	}
	// Recreate the boundary, as after restart, with only the journal's latest tick.
	f.Placement.Tick = second.Observation.Tick
	b, err = NewBoundary(f, f, f, f, FixedClock{}, "session", nil)
	if err != nil {
		t.Fatal(err)
	}
	f.Progress = completed
	f.Progress.Context.Tick = proto.Int64(13)
	final, err := b.Observe(context.Background(), f.Placement, f.Placement.Snapshot)
	if err != nil || final.Observation.Effect != domain.EffectCompleted || f.Receipt.AdmittedContext.GetTick() != 10 {
		t.Fatal("restart completion lost immutable receipt", err)
	}
	f.Progress.Context.Tick = proto.Int64(11)
	if _, err = b.Observe(context.Background(), f.Placement, f.Placement.Snapshot); err == nil {
		t.Fatal("regressing observation accepted")
	}
	// Initial dispatch still cannot accept an admission predating its inspection.
	f.Placement.Tick = 11
	if _, err = b.Place(context.Background(), f.Placement); err == nil {
		t.Fatal("old admission accepted for initial placement")
	}
}

func TestAttemptConflictDoesNotProveNoEffect(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	f.PlaceErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT.Enum()}}
	out, err := b.Place(context.Background(), f.Placement)
	if err == nil || out.Kind != domain.ReceiptUnknown {
		t.Fatal("existing attempt conflict released uncertainty", err)
	}
}

// An unknown ledger lookup is the trace of a dispatch that timed out before
// native admission (#71): it resolves as complete absence at the lookup's
// own tick. An admitted in-flight entry still holds, and a lookup context
// from before the dispatch or another native generation is never evidence.
func TestUnadmittedResolvesOnlyAnUnknownLedgerLookup(t *testing.T) {
	t.Parallel()
	_, f := NewFixture(t)
	p := f.Placement
	unknown := func(tick int64, generation uint64) *r.LookupReply {
		return &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: Identity(p.Snapshot), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(generation)}}}}
	}
	out, err := Unadmitted(unknown(14, 1), p, p.Snapshot)
	if err != nil || out.Effect != domain.EffectAbsent || out.Tick != 14 || out.Causality != domain.AfterDispatch || out.Action != p.Action.ID() || out.Attempt != p.Attempt || !out.Snapshot.Matches(p.Snapshot) {
		t.Fatal("unknown lookup not resolved as absent", err, out)
	}
	if out, err = Unadmitted(unknown(10, 1), p, p.Snapshot); err != nil || out.Effect != domain.EffectAbsent || out.Tick != 10 {
		t.Fatal("same-tick lookup rejected", err, out)
	}
	if _, err = Unadmitted(unknown(9, 1), p, p.Snapshot); !errors.Is(err, executor.ErrEvidence) {
		t.Fatal("pre-dispatch tick accepted", err)
	}
	if _, err = Unadmitted(unknown(14, 2), p, p.Snapshot); !errors.Is(err, executor.ErrAuthority) {
		t.Fatal("foreign native generation accepted", err)
	}
	inFlight := &r.LookupReply{Outcome: &r.LookupReply_InFlight{InFlight: &r.InFlight{Attempt: f.Receipt.Attempt, AdmittedContext: f.Receipt.AdmittedContext}}}
	if _, err = Unadmitted(inFlight, p, p.Snapshot); !errors.Is(err, executor.ErrHeld) {
		t.Fatal("in-flight attempt not held", err)
	}
	if _, err = Unadmitted(&r.LookupReply{}, p, p.Snapshot); !errors.Is(err, executor.ErrEvidence) {
		t.Fatal("empty lookup accepted", err)
	}
}
