package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type stoneShellNative struct {
	*sleepingNative
	buildings *o.ListBuildingsReply
	sites     bridge.WallUpgradeSites
}

func (n *stoneShellNative) ReadConstructionBuildings(ctx context.Context, _ *c.Identity, _ []string) (*o.ListBuildingsReply, bridge.Result, error) {
	return n.buildings, bridge.Result{}, ctx.Err()
}

func (n *stoneShellNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, id)
	v.Facts.Colonists = []policy.EmergencyPawn{{ID: "builder", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}
	return v, receipt, err
}

func (n *stoneShellNative) ReadWallUpgradeSites(ctx context.Context, _ *c.Identity, _ string) (bridge.WallUpgradeSites, bridge.Result, error) {
	return n.sites, bridge.Result{}, ctx.Err()
}

// stoneShellFixture journals one completed, identity-bound autonomous wooden
// wall at (4,4), reports it flammable in the Upkeep census, and offers a
// granite replacement site with no backups so the bundle is demolish +
// replace.
func stoneShellFixture(t *testing.T) (*RoutineStoneShellPlanner, *store.Store, *stoneShellNative) {
	t.Helper()
	ctx := context.Background()
	base, db, shelter := shelterFixture(t)
	n := &stoneShellNative{sleepingNative: shelter}
	v := n.reply.GetObserved()
	count := func(n uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	// The player fixture's root plan holds the single-worker capacity slot.
	submitted := playerPlan(t, db)
	for _, action := range submitted.Spec.Actions() {
		if _, err := db.Cancel(ctx, submitted.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	current := base.reviewer.player.session.State().Snapshot
	goal, err := domain.NewGoal("stone-owner", domain.AutopilotGoal, 4, current, 7)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreateGoal(ctx, goal); err != nil {
		t.Fatal(err)
	}
	g, err := db.LoadGoal(ctx, goal.ID)
	if err != nil {
		t.Fatal(err)
	}
	if g, err = db.ReviewGoal(ctx, goal.ID, g.Revision, current, 7, domain.NeedDeficit, false); err != nil {
		t.Fatal(err)
	}
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 4, Z: 4}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("owned-wall", wall)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("stone-owner-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.CommitGoalMethod(ctx, goal.ID, g.Revision, "owner-Wall", spec); err != nil {
		t.Fatal(err)
	}
	scope := current
	scope.Plan, scope.Revision = spec.ID(), spec.Revision()
	if _, err = db.ReserveAndPrepare(ctx, spec.ID(), a.ID(), store.Admission{Snapshot: scope, Tick: 7, Costs: []store.MaterialCost{}, Footprint: []domain.Cell{wall.Cell()}}); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Dispatch(ctx, spec.ID(), a.ID(), scope, 7); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Observe(ctx, spec.ID(), domain.Observation{Action: a.ID(), Attempt: 1, Snapshot: scope, Tick: 7, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch, Construction: &domain.ConstructionIdentity{Origin: "blueprint", Current: "wall-1"}}, scope); err != nil {
		t.Fatal(err)
	}
	cell := &c.Cell{X: proto.Int32(4), Z: proto.Int32(4)}
	entity := &o.EntityRef{Id: proto.String("wall-1"), DefName: proto.String("Wall"), MapId: proto.Int32(0), Position: cell}
	n.buildings = &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: &o.BuildingsSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: count(1), Buildings: []*o.BuildingState{{Building: entity, Status: proto.String("built"), Rotation: proto.String("North"), Stuff: proto.String("WoodLog")}}}}}
	v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{
		Structures:   []*o.UpkeepStructure{{Building: &o.BuildingState{Building: entity}, Flammability: proto.Float64(1)}},
		Completeness: count(1),
		Comfort:      &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}},
	}}}
	n.sites = bridge.WallUpgradeSites{Context: proto.Clone(v.Context).(*c.ObservationContext), Sites: []bridge.WallUpgradeSite{{
		TargetID: "wall-1", TargetPresent: true, X: 4, Z: 4, NX: 0, NZ: 1, LeftSupport: true, RightSupport: true,
		ReplacementMaterials: []bridge.WallMaterial{{Stuff: "BlocksGranite", Costs: []bridge.Amount{{Resource: "BlocksGranite", Units: 5}}}},
	}}}
	n.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		b, _ := p.Preview.Action.Building()
		p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
		// Native reports every Wall as made from stuff; a planner that
		// mistook that for a refusal never admitted a bundle (#293).
		p.Preview.MadeFromStuff = domain.Known(true)
		p.Preview.Costs = domain.Known([]policy.Amount{{Resource: "BlocksGranite", Count: 5}})
		p.Stock.Values = []policy.Stock{{Resource: "BlocksGranite", Available: domain.Known(int64(100))}}
	}
	// Development ranking needs a known construction-capable worker census.
	v.ColonistCount, v.WorkerCount = proto.Uint32(1), proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("builder"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	row.Biography.Skills = append(row.Biography.Skills, &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String("Construction")}, Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: proto.String("None")})
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Construction"), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: count(1)}}}
	base.reviewer.native = n
	base.reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainStoneShell})
	if _, err = base.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineStoneShellPlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, n
}

// The merged stock observation must carry the current snapshot and tick;
// a zero StockObservation makes admission refuse every action as stale_facts.
func TestRoutineStoneShellAdmitsReplacementBundleWithFreshStock(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	p, db, n := stoneShellFixture(t)
	result, err := p.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted || n.previews != 1 {
		t.Fatal(result, err, n.previews)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 2 || len(plan.Admissions) != 1 {
		t.Fatal(plan, err)
	}
	actions := plan.Spec.Actions()
	removal, ok := actions[0].WallRemoval()
	if !ok || removal.Original() != "wall-1" {
		t.Fatal(actions[0])
	}
	replacement, ok := actions[1].Building()
	if !ok || replacement.Definition() != "Wall" || replacement.Cell() != (domain.Cell{X: 4, Z: 4}) || replacement.Stuff() != "BlocksGranite" {
		t.Fatal(actions[1])
	}
	admission := plan.Admissions[0]
	if admission.Action != actions[1].ID() || admission.Admission.Tick != 7 || len(admission.Admission.Costs) != 1 || admission.Admission.Costs[0] != (store.MaterialCost{Definition: "BlocksGranite", Count: 5}) {
		t.Fatal(admission)
	}
	if deps := plan.Spec.Dependencies(); len(deps) != 1 || deps[0] != (domain.ActionDependency{Action: actions[1].ID(), Requires: actions[0].ID()}) {
		t.Fatal(deps)
	}
	for _, progress := range plan.Progress {
		if v := progress.View(); v.Stage != domain.Pending || v.Unresolved {
			t.Fatal(progress)
		}
	}
	if again, err := p.Step(ctx); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
	// The pending demolition is clock work like the wall build after it: a
	// window that held on the WallRemovalAction never ran the bundle (#293).
	target := p.reviewer.player.session.State().Snapshot
	target.Plan, target.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if work, _, err := clockSchedulerWork(plan, target); err != nil || !work {
		t.Fatal("stone shell bundle cannot advance", work, err)
	}
}

// A stock observation without the current snapshot and tick is exactly what
// the planner used to build: admission must refuse it as stale_facts rather
// than admit against unverified stock.
func TestRoutineStoneShellRefusesZeroStockObservationAsStaleFacts(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	p, db, n := stoneShellFixture(t)
	n.onPreview = func(_ context.Context, v *bridge.BuildingPreview) {
		b, _ := v.Preview.Action.Building()
		v.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
		v.Preview.MadeFromStuff = domain.Known(true)
		v.Preview.Costs = domain.Known([]policy.Amount{{Resource: "BlocksGranite", Count: 5}})
		v.Stock.Values = []policy.Stock{{Resource: "BlocksGranite", Available: domain.Known(int64(100))}}
	}
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainStoneShell {
			if goal, err = db.LoadGoal(ctx, binding.Goal); err != nil {
				t.Fatal(err)
			}
		}
	}
	snapshot := p.reviewer.player.session.State().Snapshot
	snapshot.Plan, snapshot.Revision = "stale-stock-plan", 1
	wall, _ := domain.NewBuilding("Wall", domain.Cell{X: 4, Z: 4}, domain.North, "BlocksGranite")
	action, _ := domain.NewBuildingAction("stale-stock-replace", wall)
	preview, _, err := n.PreviewBuilding(ctx, action, snapshot)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("stale-stock-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	request := store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: "stale-stock", Plan: spec, Current: snapshot, Tick: 7, Bounds: domain.Known(policy.Bounds{Width: 9, Height: 9}), Rules: p.reviewer.rules, Previews: []policy.Preview{preview.Preview}, Purpose: policy.Routine}
	var zero policy.StockObservation
	if err := mergeRoutineStock(&zero, preview.Stock, true); err != nil {
		t.Fatal(err)
	}
	request.Stock = zero
	decision, err := db.AdmitBuildingMethod(ctx, request)
	if err != nil || decision.Admitted || len(decision.Refused) != 1 || decision.Refused[0].Reason != policy.StaleFacts {
		t.Fatal(decision, err)
	}
	fresh := policy.StockObservation{Snapshot: snapshot, Tick: 7}
	if err := mergeRoutineStock(&fresh, preview.Stock, true); err != nil {
		t.Fatal(err)
	}
	request.Stock = fresh
	if decision, err = db.AdmitBuildingMethod(ctx, request); err != nil || !decision.Admitted || len(decision.Refused) != 0 {
		t.Fatal(decision, err)
	}
}
