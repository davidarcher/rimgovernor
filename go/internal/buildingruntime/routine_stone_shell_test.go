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
	return stoneShellFixtureHistory(t, true)
}

func stoneShellFixtureHistory(t *testing.T, history bool) (*RoutineStoneShellPlanner, *store.Store, *stoneShellNative) {
	t.Helper()
	ctx := context.Background()
	base, db, shelter := shelterFixture(t)
	n := &stoneShellNative{sleepingNative: shelter}
	v := n.reply.GetObserved()
	count := func(n uint64) *o.Completeness {
		return &o.Completeness{Filtered: proto.Uint64(0)}
	}
	// The player fixture's root plan holds the single-worker capacity slot.
	submitted := playerPlan(t, db)
	for _, action := range submitted.Spec.Actions() {
		if _, err := db.Cancel(ctx, submitted.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	current := base.reviewer.player.session.State().Snapshot
	var err error
	if history {
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
		if g, err = db.ReviewGoal(ctx, goal.ID, g.Revision, current, 7, domain.NeedDeficit); err != nil {
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
		if _, err = db.Prepare(ctx, spec.ID(), a.ID(), scope, 7); err != nil {
			t.Fatal(err)
		}
		if _, err = db.Dispatch(ctx, spec.ID(), a.ID(), scope, 7); err != nil {
			t.Fatal(err)
		}
		if _, err = db.RecordReceipt(ctx, spec.ID(), a.ID(), 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
	}
	cell := &c.Cell{X: proto.Int32(4), Z: proto.Int32(4)}
	entity := &o.EntityRef{Id: proto.String("wall-1"), DefName: proto.String("Wall"), MapId: proto.Int32(0), Position: cell}
	n.buildings = &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: &o.BuildingsSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: count(1), Buildings: []*o.BuildingState{{Building: entity, OccupiedCells: []*c.Cell{cell}, Status: proto.String("built"), Rotation: proto.String("North"), Stuff: proto.String("WoodLog")}}}}}
	v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{
		Structures: []*o.UpkeepStructure{{Building: &o.BuildingState{Building: entity}, Flammability: proto.Float64(1)}},
		Comfort:    &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}},
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
	// The stone shell is a Stable goal; the fixture has not climbed there.
	base.reviewer.policy.Stage.Floor = policy.StageStable
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
	if err != nil || len(plan.Progress) != 2 {
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
	if len(plan.Admissions) != 0 {
		t.Fatal(plan.Admissions)
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

func TestRoutineStoneShellAdmitsPlayerBuiltWall(t *testing.T) {
	p, db, _ := stoneShellFixtureHistory(t, false)
	claims, err := db.ConstructionClaims(context.Background(), p.reviewer.player.session.State().Snapshot, 7)
	rows, known := claims.Value()
	// The shelter bunks are open claims of their own; only a wall claim
	// would make this a history the player did not build.
	walls := 0
	for _, row := range rows {
		if row.Building.Definition() == "Wall" {
			walls++
		}
	}
	if err != nil || !known || walls != 0 {
		t.Fatal("fixture has wall construction history", rows, known, err)
	}
	result, err := p.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
}

func TestRoutineHomeCoverageAdmitsPlayerBuiltFacility(t *testing.T) {
	stone, db, native := stoneShellFixtureHistory(t, false)
	v := native.reply.GetObserved()
	v.Upkeep.GetObserved().HomeCoverage = &o.HomeCoverageSection{Outcome: &o.HomeCoverageSection_Observed{Observed: &o.HomeCoverageFacts{
		Revision: proto.Int64(1),
		Targets:  []*o.HomeCoverageTarget{{Id: proto.String("wall-1"), ShapeToken: proto.String("shape"), MissingCells: proto.Uint32(1), ExcludedCells: proto.Uint32(0), Cells: []*c.Cell{{X: proto.Int32(4), Z: proto.Int32(4)}}, ExtentGeometry: &o.HomeExtentGeometry{}}},
	}}}
	v.Upkeep.GetObserved().AutoHomeArea = proto.Bool(true)
	stone.reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainHomeCoverage})
	if _, err := stone.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineHomeCoveragePlanner(stone.reviewer)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil || len(plan.Spec.Actions()) != 2 || plan.Spec.Actions()[0].Kind() != domain.AutoHomeAreaAction || plan.Spec.Actions()[1].Kind() != domain.AreaAction {
		t.Fatal(plan, err)
	}
}

func (n *stoneShellNative) ReadRoutineFrame(ctx context.Context, id *c.Identity, definitions []string) (bridge.RoutineFrame, error) {
	return fakeFrame(ctx, n, id, definitions)
}
