package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// rockCoolerNative is the refrigeration fixture with natural rock on the
// planned cooler cell and its shaft, and a preview over rock (#874).
type rockCoolerNative struct {
	*refrigerationNative
	rock      map[domain.Cell]bool
	overRock  int
	overCells []domain.Cell
}

func (n *rockCoolerNative) ReadExcavationSite(ctx context.Context, _ *c.Identity, cells []domain.Cell, _ domain.Cell) (bridge.ExcavationSite, bridge.Result, error) {
	site := bridge.ExcavationSite{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Support: policy.ExcavationSupportSupported, WorkerAvailable: true, AccessReachable: true, Workers: []string{"miner"}}
	for _, cell := range cells {
		row := bridge.ExcavationSiteCell{Cell: cell}
		if n.rock[cell] {
			row.Definition, row.Eligible = "Granite", true
		} else {
			row.Walkable = true
		}
		site.Cells = append(site.Cells, row)
	}
	return site, bridge.Result{}, ctx.Err()
}

func (n *rockCoolerNative) PreviewBuildingOverRock(ctx context.Context, action domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	n.overRock++
	b, _ := action.Building()
	n.overCells = append(n.overCells, b.Cell())
	return n.PreviewBuilding(ctx, action, s)
}

// A standing freezer whose planned cooler cell is rock mines that cell and
// the shaft and places the cooler in one plan, the cooler waiting on every
// excavation and previewed over rock (#874).
func TestExhaustDigMinesRockCoolerCellAndPlacesCoolerInOnePlan(t *testing.T) {
	t.Parallel()
	p, db, base, _ := refrigerationFixture(t, false)
	site := policy.PlannedCoolerSite{Cell: domain.Cell{X: 1, Z: 3}, Rotation: domain.North}
	shaft := domain.Cell{X: 1, Z: 4}
	n := &rockCoolerNative{refrigerationNative: base, rock: map[domain.Cell]bool{site.Cell: true, shaft: true}}
	p.native = n
	ctx := context.Background()
	state := p.reviewer.player.session.State()
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goalID domain.GoalID
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainRefrigeration {
			goalID = binding.Goal
		}
	}
	goal, err := db.LoadGoal(ctx, goalID)
	if err != nil {
		t.Fatal(err)
	}
	identity, _, err := n.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		t.Fatal(err)
	}
	reading, err := p.reviewer.observeRooms(ctx, n, expected, domain.Unknown[[]policy.ConstructionClaim](), "Cooler")
	if err != nil {
		t.Fatal(err)
	}
	s := excavationStep{state: state, review: review, goal: goal, facts: reading.Projection, read: reading.ColonyReading}
	cold := policy.RefrigerationCooler{Position: site.Cell, Rotation: site.Rotation}.Cold()
	method := domain.MethodID("plan-dig-exhaust-test")
	result, handled, err := p.digPlanned(ctx, ctx, s, []domain.Cell{site.Cell, shaft}, cold, method, &site, func() error { return nil })
	if err != nil || !handled || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, handled, err)
	}
	if n.overRock != 1 || len(n.overCells) != 1 || n.overCells[0] != site.Cell {
		t.Fatal("over-rock previews", n.overRock, n.overCells)
	}
	plan, err := db.LoadPlan(ctx, methodPlan(t, result.Decision, method))
	if err != nil {
		t.Fatal(err)
	}
	actions := plan.Spec.Actions()
	if len(actions) != 3 {
		t.Fatal(actions)
	}
	b, ok := actions[0].Building()
	if !ok || b.Definition() != "Cooler" || b.Cell() != site.Cell || b.Rotation() != site.Rotation {
		t.Fatal(actions[0])
	}
	dug := map[domain.Cell]bool{}
	requires := map[domain.ActionID]bool{}
	for _, dep := range plan.Spec.Dependencies() {
		if dep.Action != actions[0].ID() {
			t.Fatal("dependency on a non-cooler action", dep)
		}
		requires[dep.Requires] = true
	}
	for _, action := range actions[1:] {
		excavation, ok := action.Excavation()
		if !ok || !requires[action.ID()] {
			t.Fatal("excavation not required by the cooler", action)
		}
		dug[excavation.Cell()] = true
	}
	if !dug[site.Cell] || !dug[shaft] || len(requires) != 2 {
		t.Fatal(dug, requires)
	}
}
