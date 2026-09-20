package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type drillNative struct {
	*resourceNative
	existing bool
}

func (n *drillNative) ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error) {
	return bridge.ResearchRead{Context: n.reply.GetObserved().Context, Finished: []string{"DeepDrilling", "GroundPenetratingScanner"}}, bridge.Result{}, nil
}
func (n *drillNative) ReadBuildings(context.Context, *c.Identity, int64) (bridge.EntityRows[*o.BuildingState], bridge.Result, error) {
	rows := map[string]*o.BuildingState{}
	if n.existing {
		rows["drill"] = &o.BuildingState{BuildDefName: proto.String("DeepDrill")}
	}
	for _, drill := range n.reply.GetObserved().GetDeepResources().GetObserved().GetDrills() {
		rows[drill.GetBuildingId()] = &o.BuildingState{Building: &o.EntityRef{Id: proto.String(drill.GetBuildingId()), DefName: proto.String("DeepDrill")}}
	}
	return bridge.EntityRows[*o.BuildingState]{Context: n.reply.GetObserved().Context, Rows: rows}, bridge.Result{}, nil
}

type drillDispatch struct {
	planner  *RoutineResourcePlanner
	call     context.Context
	epoch    context.Context
	state    ControlState
	goal     store.GoalState
	review   store.RoutineReview
	db       *store.Store
	sleeping *sleepingNative
	base     *RoutineBuildingPlanner
}

func deepDrillDispatchFixture(t *testing.T, existing bool, drills []*o.DeepDrillState) drillDispatch {
	t.Helper()
	base, db, session, _, sleeping := sleepingFixture(t)
	base.reviewer.policy.ResourceTargets = map[policy.Resource]int64{"Steel": 100}
	v := sleeping.reply.GetObserved()
	v.Resources = []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(0)}}
	v.ColonistCount, v.WorkerCount = proto.Uint32(2), proto.Uint32(2)
	v.DeepResources = &o.DeepResourcesSection{Outcome: &o.DeepResourcesSection_Observed{Observed: &o.DeepResourcesFacts{
		GroundScanners: []*o.MineralScannerState{{BuildingId: proto.String("scanner"), DefName: proto.String("GroundPenetratingScanner"), Built: proto.Bool(true), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}}},
		Lumps: []*o.DeepResourceLump{
			{DefName: proto.String("Steel"), Count: proto.Int64(100), CellCount: proto.Uint32(1), Centre: &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}},
			{DefName: proto.String("Steel"), Count: proto.Int64(100), CellCount: proto.Uint32(1), Centre: &c.Cell{X: proto.Int32(3), Z: proto.Int32(2)}},
		},
		Drills: drills,
	}}}
	v.Planning.GetObserved().Definitions = []*o.PlanningDefinition{{Definition: &o.DefinitionRef{DefName: proto.String("DeepDrill")}, Available: proto.Bool(true), ConstructionSkill: proto.Int32(0), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(2)}}}
	for _, cell := range v.Planning.GetObserved().Cells.Cells {
		cell.Roof = nil
		cell.Issues = append(cell.Issues, &o.ReadIssue{Field: proto.String("roof"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
		cell.Indoors = proto.Bool(false)
	}
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	var pawns []*o.PawnState
	for _, id := range []string{"crafter", "builder"} {
		pawns = append(pawns, &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}})
	}
	sleeping.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: v.Context, Pawns: pawns, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	native := &drillNative{resourceNative: &resourceNative{workshopNative: &workshopNative{sleepingNative: sleeping}}, existing: existing}
	base.reviewer.native = native
	review, err := base.reviewer.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	var goal store.GoalState
	for _, binding := range review.Review.Goals {
		if binding.Need == policy.MaintainResource {
			goal, err = db.LoadGoal(context.Background(), binding.Goal)
			if err != nil {
				t.Fatal(err)
			}
		}
	}
	review.Review.ResourceRunways = []store.ResourceRunwayRecord{{Resource: "Steel", Deficit: proto.Bool(true), Target: 100}}
	planner, err := NewRoutineResourcePlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	sleeping.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		b, _ := p.Preview.Action.Building()
		p.Preview.WatchCellsAccessible = domain.Known(b.Cell().X != 2)
	}
	call, epoch, done, err := base.reviewer.player.enter(context.Background(), false)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(done)
	return drillDispatch{planner: planner, call: call, epoch: epoch, state: session.State(), goal: goal, review: review.Review, db: db, sleeping: sleeping, base: base}
}

func (d drillDispatch) run(t *testing.T) (RoutineResourceResult, bool, error) {
	t.Helper()
	return d.planner.deepDrill(d.call, d.epoch, d.state, d.goal, d.review, d.base.reviewer.clock.Now())
}

func TestDeepDrillAdmitsNearestReachableSiteAndHoldsExistingDrill(t *testing.T) {
	for _, existing := range []bool{false, true} {
		t.Run(map[bool]string{false: "placement", true: "existing"}[existing], func(t *testing.T) {
			d := deepDrillDispatchFixture(t, existing, nil)
			result, handled, err := d.run(t)
			if existing {
				if err != nil || !handled || result.Reason != BuildingMethodExistingWork || d.sleeping.previews != 0 {
					t.Fatal(result, handled, err)
				}
				return
			}
			if err != nil || !handled || result.Reason != BuildingMethodAdmitted {
				t.Fatal(result, handled, err, d.review.Development.Rows)
			}
			plan, err := d.db.LoadPlan(context.Background(), result.Plan)
			if err != nil {
				t.Fatal(err)
			}
			building, ok := plan.Spec.Actions()[0].Building()
			if !ok || building.Definition() != "DeepDrill" || building.Cell() != (domain.Cell{X: 3, Z: 2}) || d.sleeping.previews != 2 {
				t.Fatal(building, d.sleeping.previews)
			}
			if plan.Progress[0].View().Stage != domain.Pending {
				t.Fatal("placement bypassed Hands")
			}
		})
	}
}

func drillRow(owned, depleted, designated bool) *o.DeepDrillState {
	row := &o.DeepDrillState{BuildingId: proto.String("Thing_DeepDrill_7"), DefName: proto.String("DeepDrill"), Position: &c.Cell{X: proto.Int32(4), Z: proto.Int32(1)},
		Powered: proto.Bool(true), Depleted: proto.Bool(depleted), ControllerOwned: proto.Bool(owned), Designated: proto.Bool(designated)}
	if !depleted {
		row.Resource, row.Remaining = proto.String("Steel"), proto.Int64(40)
	}
	return row
}

// Only a controller-owned drill whose seam the native census reads as depleted
// is removed, and only through a Hands-dispatched drill Deconstruction; every
// other drill (player-built, still yielding, already designated) holds
// placement exactly as before (#538).
func TestDeepDrillRemovesOnlyExhaustedOwnedDrills(t *testing.T) {
	for _, tc := range []struct {
		name                        string
		owned, depleted, designated bool
		removal                     bool
	}{
		{"owned exhausted", true, true, false, true},
		{"player exhausted", false, true, false, false},
		{"owned yielding", true, false, false, false},
		{"owned exhausted designated", true, true, true, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			d := deepDrillDispatchFixture(t, false, []*o.DeepDrillState{drillRow(tc.owned, tc.depleted, tc.designated)})
			result, handled, err := d.run(t)
			if err != nil || !handled {
				t.Fatal(result, handled, err)
			}
			if !tc.removal {
				if result.Reason != BuildingMethodExistingWork || d.sleeping.previews != 0 {
					t.Fatal(result, d.sleeping.previews)
				}
				return
			}
			if result.Reason != BuildingMethodAdmitted || d.sleeping.previews != 0 {
				t.Fatal(result, d.sleeping.previews)
			}
			plan, err := d.db.LoadPlan(context.Background(), result.Plan)
			if err != nil {
				t.Fatal(err)
			}
			cut, ok := plan.Spec.Actions()[0].Deconstruction()
			if !ok || !cut.Drill() || cut.Breach() || cut.Target() != "Thing_DeepDrill_7" || cut.Definition() != "DeepDrill" || cut.Cell() != (domain.Cell{X: 4, Z: 1}) {
				t.Fatal(cut)
			}
			if plan.Progress[0].View().Stage != domain.Pending {
				t.Fatal("removal bypassed Hands")
			}
			// A second pass on the same episode commits the next attempt only
			// once the first plan is closed; the goal's open work holds it.
			goal, err := d.db.LoadGoal(context.Background(), d.goal.Goal.ID)
			if err != nil || len(goal.Methods) != 1 || goal.Methods[0].Method != "deconstruct-drill-Thing_DeepDrill_7-0" {
				t.Fatal(goal.Methods, err)
			}
		})
	}
}

func TestExhaustedDrillsRequireKnownOwnershipAndDepletion(t *testing.T) {
	known := observation.DeepDrill{ID: "b", ControllerOwned: domain.Known(true), Depleted: domain.Known(true), Designated: domain.Known(false)}
	unknownOwner := known
	unknownOwner.ID, unknownOwner.ControllerOwned = "a", domain.Unknown[bool]()
	unknownDepletion := known
	unknownDepletion.ID, unknownDepletion.Depleted = "c", domain.Unknown[bool]()
	second := known
	second.ID = "a2"
	out := exhaustedDrills(observation.DeepResources{Drills: []observation.DeepDrill{known, unknownOwner, unknownDepletion, second}})
	if len(out) != 2 || out[0].ID != "a2" || out[1].ID != "b" {
		t.Fatal(out)
	}
}
