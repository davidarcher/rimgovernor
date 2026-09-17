package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

type fieldTestNative struct {
	*routineNative
	changed bool
}

func (n *fieldTestNative) PreviewZone(ctx context.Context, id *c.Identity, target bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error) {
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Accepted: proto.Bool(true)}}}, bridge.Result{}, nil
}
func TestFieldPlannerReservationsCASAndManual(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	base, db, session, request, n := sleepingFixture(t)
	reviewer := base.reviewer
	reviewer.methods = domain.Known([]policy.GoalID{policy.EnsureFoodSupply})
	v := n.reply.GetObserved()
	v.Farms = nil
	v.FoodClimate = &o.FoodClimate{GrowingDays: proto.Float64(60), GrowingDaysRemaining: proto.Float64(60), SowingNow: proto.Bool(true)}
	issues := v.Issues[:0]
	for _, i := range v.Issues {
		if i.GetField() != "farms" && i.GetField() != "food_climate" {
			issues = append(issues, i)
		}
	}
	v.Issues = issues
	planning := v.Planning.GetObserved()
	planning.ZoneMapSnapshot = &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String(fmt.Sprintf("map-%d", v.Context.Identity.GetMapId())), Token: proto.String("zone-map")}
	planning.Definitions = []*o.PlanningDefinition{{Definition: &o.DefinitionRef{DefName: proto.String("Plant_Rice")}, Available: proto.Bool(true), Edible: proto.Bool(true), GrowDays: proto.Float64(3), FertilityMin: proto.Float64(.7), FertilitySensitivity: proto.Float64(1), HarvestNutrition: proto.Float64(1), NutritionDemandPerDay: proto.Float64(5)}}
	for _, cell := range planning.Cells.Cells {
		cell.Roof = nil
		cell.Fertility = proto.Float64(1)
		cell.Issues = append(cell.Issues, &o.ReadIssue{Field: proto.String("roof"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineFieldPlanner(reviewer, &fieldTestNative{routineNative: n.routineNative})
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Admissions) != len(plan.Progress) || len(plan.Progress) == 0 {
		t.Fatal(plan, err)
	}
	snapshot := session.State().Snapshot
	snapshot.Plan = result.Plan
	snapshot.Revision = 1
	if err := db.AuthorizeRoutinePlan(ctx, session.State().Snapshot, snapshot); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, snapshot); err != nil || work {
		t.Fatal("creation needs no ticks", work, err)
	}
	a := plan.Spec.Actions()[0]
	tick := domain.Tick(v.Context.GetTick())
	if _, err := db.PrepareZone(ctx, result.Plan, a.ID(), store.ZoneAdmission{Snapshot: snapshot, Tick: tick, SnapshotToken: "fresh-map"}); err != nil {
		t.Fatal(err)
	}
	stored, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(stored.ZoneAdmissions) != 1 || stored.ZoneAdmissions[0].Admission.SnapshotToken != "fresh-map" {
		t.Fatal(stored, err)
	}

	// The shared reservation cannot bypass typed map preparation.
	if _, err := db.ReserveAndPrepare(ctx, result.Plan, a.ID(), plan.Admissions[0].Admission); err == nil {
		t.Fatal("generic zone preparation bypass")
	}
	dispatched, err := db.Dispatch(ctx, result.Plan, a.ID(), snapshot, tick)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := db.Observe(ctx, result.Plan, domain.Observation{Action: a.ID(), Attempt: dispatched.View().Attempt, Snapshot: snapshot, Tick: tick + 1, Effect: domain.EffectCompleted}, snapshot); err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal domain.Goal
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureFoodSupply {
			g, err := db.LoadGoal(ctx, binding.Goal)
			if err != nil {
				t.Fatal(err)
			}
			goal = g.Goal
		}
	}
	n.reply.GetObserved().Context.Tick = proto.Int64(int64(tick + 1))
	facts := observation.ColonyProjection{Identity: observation.Identity{Tick: tick + 1}, Definitions: []observation.PlanningDefinition{{Name: "Plant_Rice", GrowDays: domain.Known(3.0)}}}
	allowance, managed, err := planner.fieldAllowance(ctx, goal, session.State().Snapshot, facts)
	if err != nil || allowance == 0 || len(managed) != 1 {
		t.Fatal("growth budget", allowance, managed, err)
	}
	facts.Identity.Tick += domain.Tick(allowance)
	if wait, _, err := planner.fieldAllowance(ctx, goal, session.State().Snapshot, facts); err != nil || wait != 0 {
		t.Fatal("deadline renewed", wait, err)
	}
	facts.Identity.Tick = tick + 1
	planner.native.(*fieldTestNative).changed = true
	if wait, _, err := planner.fieldAllowance(ctx, goal, session.State().Snapshot, facts); err != nil || wait != 0 {
		t.Fatal("changed field granted time", wait, err)
	}

	if len(plan.Spec.Actions()) < 2 {
		t.Fatal("need second patch")
	}
	second := plan.Spec.Actions()[1]
	boundary := &fieldExecutorTest{clock: reviewer.clock, tick: tick + 1}
	worker, err := executor.New(db, boundary, reviewer.clock, executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = worker.EnableZone(boundary); err != nil {
		t.Fatal(err)
	}
	if err = worker.UpdateAuthority(executor.Authority{Snapshot: snapshot, Enabled: true}); err != nil {
		t.Fatal(err)
	}
	attempt, err := worker.Run(ctx, result.Plan, second.ID())
	if err == nil || !attempt.Progress.View().Unresolved || boundary.writes != 1 {
		t.Fatal("lost reply", attempt, err)
	}
	if _, err = worker.Run(ctx, result.Plan, second.ID()); err != nil || boundary.writes != 1 {
		t.Fatal("uncertainty retried", err)
	}
	request.Kind = store.PauseControl
	request.RequestID = "manual-fields"
	if _, err := reviewer.player.Pause(ctx, request); err != nil {
		t.Fatal(err)
	}

	if err = worker.UpdateAuthority(executor.Authority{Snapshot: snapshot, Enabled: false}); err != nil {
		t.Fatal(err)
	}
	boundary.complete = true
	recovered, err := worker.Run(ctx, result.Plan, second.ID())
	if err != nil || recovered.Progress.View().Unresolved || recovered.Progress.View().Effect != domain.Known(domain.EffectCompleted) || boundary.writes != 1 {
		t.Fatal("manual observation", recovered, err)
	}
	stored, err = db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	// Manual suspends the goal: the remaining patches stay pending for the
	// resumed goal rather than being cancelled.
	for _, p := range stored.Progress {
		if p.Action().ID() != a.ID() && p.Action().ID() != second.ID() && p.View().Stage != domain.Pending {
			t.Fatal(p)
		}
	}
}

func (n *fieldTestNative) LookupZone(ctx context.Context, w bridge.ZoneAttempt) (*r.LookupReply, bridge.Result, error) {
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: &r.Receipt{Attempt: w.Attempt}}}, bridge.Result{}, nil
}
func (n *fieldTestNative) ObserveZone(ctx context.Context, w bridge.ZoneAttempt, receipt *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	token := bridge.ZoneConfigurationToken(w.Zone)
	if n.changed {
		token = "changed"
	}
	d := &r.ZoneEffect{ZoneId: proto.String("field"), Present: proto.Bool(true), ListedCellCount: proto.Int32(int32(len(w.Zone.Cells()))), GridCellCount: proto.Int32(int32(len(w.Zone.Cells()))), ChangedCells: proto.Int32(int32(len(w.Zone.Cells()))), PhantomCellCount: proto.Int32(0), Snapshot: &r.SnapshotEvidence{EntityId: proto.String("field"), BeforeToken: proto.String(w.Token), AfterToken: proto.String(token)}}
	for _, cell := range w.Zone.Cells() {
		d.Cells = append(d.Cells, &r.CellResult{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, Accepted: proto.Bool(true)})
	}
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: &r.Progress{Attempt: w.Attempt, Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Zone{Zone: d}}}}}}}, bridge.Result{}, nil
}

type fieldExecutorTest struct {
	executor.Boundary
	clock    executor.Clock
	tick     domain.Tick
	writes   int
	complete bool
}

func (n *fieldExecutorTest) InspectZone(ctx context.Context, target executor.Target) (executor.ZoneInspection, error) {
	zone, _ := target.Action.ZoneCreate()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)})
	return executor.ZoneInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Zone: zone, SnapshotToken: "fresh-map", Accepted: true, Emergency: emergency}, nil
}
func (n *fieldExecutorTest) CreateZone(ctx context.Context, d executor.ZoneDispatch) (executor.Receipt, error) {
	n.writes++
	return executor.Receipt{}, errors.New("lost native reply")
}
func (n *fieldExecutorTest) ObserveZone(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.ZoneEvidence, error) {
	zone, _ := p.Action.ZoneCreate()
	effect := domain.EffectUnknown
	if n.complete {
		effect = domain.EffectCompleted
	}
	return executor.ZoneEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: n.tick + 2, Effect: effect, Causality: domain.AfterDispatch}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: n.complete, Zone: zone, Matches: domain.Known(n.complete)}, nil
}

// Open hunting or foraging under EnsureFoodSupply must not starve the field
// planner; only open zone work does.
func TestFieldBlockingWorkIgnoresAcquisition(t *testing.T) {
	t.Parallel()
	hunt := dispatchedHunt(t, "hunt-deer", "deer", 100)
	if fieldBlockingWork([]domain.Progress{hunt}) {
		t.Fatal("open hunt blocked fields")
	}
	zone, err := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewZoneCreateAction("field-1", zone)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("field-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewProgress(spec, a.ID())
	if err != nil {
		t.Fatal(err)
	}
	if !fieldBlockingWork([]domain.Progress{hunt, p}) {
		t.Fatal("pending zone did not block fields")
	}
	if p, err = p.Cancel(); err != nil {
		t.Fatal(err)
	}
	if fieldBlockingWork([]domain.Progress{p}) {
		t.Fatal("cancelled zone blocked fields")
	}
}
