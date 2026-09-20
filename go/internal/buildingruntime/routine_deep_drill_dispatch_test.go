package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
	return bridge.EntityRows[*o.BuildingState]{Context: n.reply.GetObserved().Context, Rows: rows}, bridge.Result{}, nil
}

func TestDeepDrillAdmitsNearestReachableSiteAndHoldsExistingDrill(t *testing.T) {
	for _, existing := range []bool{false, true} {
		t.Run(map[bool]string{false: "placement", true: "existing"}[existing], func(t *testing.T) {
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
			ctx := context.Background()
			call, epoch, done, err := base.reviewer.player.enter(ctx, false)
			if err != nil {
				t.Fatal(err)
			}
			defer done()
			result, handled, err := planner.deepDrill(call, epoch, session.State(), goal, review.Review, base.reviewer.clock.Now())
			if existing {
				if err != nil || !handled || result.Reason != BuildingMethodExistingWork || sleeping.previews != 0 {
					t.Fatal(result, handled, err)
				}
				return
			}
			if err != nil || !handled || result.Reason != BuildingMethodAdmitted {
				t.Fatal(result, handled, err, review.Review.Development.Rows)
			}
			plan, err := db.LoadPlan(ctx, result.Plan)
			if err != nil {
				t.Fatal(err)
			}
			building, ok := plan.Spec.Actions()[0].Building()
			if !ok || building.Definition() != "DeepDrill" || building.Cell() != (domain.Cell{X: 3, Z: 2}) || sleeping.previews != 2 {
				t.Fatal(building, sleeping.previews)
			}
			if plan.Progress[0].View().Stage != domain.Pending {
				t.Fatal("placement bypassed Hands")
			}
		})
	}
}
