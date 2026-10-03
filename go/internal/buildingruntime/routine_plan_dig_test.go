package buildingruntime

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// rockCoolerNative is the refrigeration fixture with natural rock on the
// planned cooler cell and its shaft, and a preview over rock (#874).
type rockCoolerNative struct {
	*refrigerationNative
	rock      map[domain.Cell]bool
	overRock  int
	refuse    bool
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
	preview, raw, err := n.PreviewBuilding(ctx, action, s)
	if n.refuse {
		preview.Preview.CanPlace = domain.Known(false)
	}
	return preview, raw, err
}

// rockCoolerStep is the refrigeration fixture with rock on a planned cooler
// cell and its shaft, and the excavation step a dig plan runs from.
func rockCoolerStep(t *testing.T) (p *RoutineBuildingPlanner, db *store.Store, n *rockCoolerNative, s excavationStep, site policy.PlannedCoolerSite, shaft domain.Cell) {
	t.Helper()
	p, db, base, _ := refrigerationFixture(t, false)
	site = policy.PlannedCoolerSite{Cell: domain.Cell{X: 1, Z: 3}, Rotation: domain.North}
	shaft = domain.Cell{X: 1, Z: 4}
	n = &rockCoolerNative{refrigerationNative: base, rock: map[domain.Cell]bool{site.Cell: true, shaft: true}}
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
	s = excavationStep{state: state, review: review, goal: goal, facts: reading.Projection, read: reading.ColonyReading}
	return p, db, n, s, site, shaft
}

// A standing freezer whose planned cooler cell is rock mines that cell and
// the shaft and places the cooler in one plan, the cooler waiting on every
// excavation and previewed over rock (#874).
func TestExhaustDigMinesRockCoolerCellAndPlacesCoolerInOnePlan(t *testing.T) {
	t.Parallel()
	p, db, n, s, site, shaft := rockCoolerStep(t)
	ctx := context.Background()
	coolerBuilding, err := domain.NewBuilding("Cooler", site.Cell, site.Rotation, "")
	if err != nil {
		t.Fatal(err)
	}
	cold := policy.RefrigerationCooler{Position: site.Cell, Rotation: site.Rotation}.Cold()
	method := domain.MethodID("plan-dig-exhaust-test")
	planned := []policy.RoleCell{{Cell: site.Cell, Role: policy.RockNeedsFloor}, {Cell: shaft, Role: policy.RockNeedsFloor}}
	// Cells the frame lists open need no dig: nothing is admitted.
	if _, handled, err := p.admitRockStep(ctx, ctx, s, planned, cold, "plan-dig-open", []domain.Building{coolerBuilding}, func() error { return nil }); err != nil || handled {
		t.Fatal("open ground handled", handled, err)
	}
	// A native refusal naming no blocker on a building cell the frame lists
	// open is an error, not a retryable refusal.
	n.refuse = true
	open, err := domain.NewBuilding("Cooler", domain.Cell{X: 1, Z: 2}, site.Rotation, "")
	if err != nil {
		t.Fatal(err)
	}
	rockOnly := []policy.RoleCell{{Cell: shaft, Role: policy.RockNeedsFloor}}
	for i, c := range s.facts.Cells {
		if c.Cell == shaft {
			s.facts.Cells[i].Occupied, s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof, s.facts.Cells[i].NaturalRock = domain.Known(true), domain.Known(false), domain.Known("RoofRockThick"), domain.Known(true)
		}
	}
	if _, _, err := p.admitRockStep(ctx, ctx, s, rockOnly, cold, "plan-dig-refused", []domain.Building{open}, func() error { return nil }); err == nil {
		t.Fatal("refusal on an open cell was not an error")
	}
	n.refuse, n.overRock, n.overCells = false, 0, nil
	for i, c := range s.facts.Cells {
		if c.Cell == site.Cell || c.Cell == shaft {
			s.facts.Cells[i].Occupied, s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof, s.facts.Cells[i].NaturalRock = domain.Known(true), domain.Known(false), domain.Known("RoofRockThick"), domain.Known(true)
		}
	}
	result, handled, err := p.admitRockStep(ctx, ctx, s, planned, cold, method, []domain.Building{coolerBuilding}, func() error { return nil })
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

// A dig plan that settled with rock still standing refuses at once, naming
// the rock: no follow-up rounds (#1588).
func TestDigPlannedRefusesLoudlyWhenADigSettlesWithRockStanding(t *testing.T) {
	t.Parallel()
	p, db, _, s, site, shaft := rockCoolerStep(t)
	ctx := context.Background()
	cold := policy.RefrigerationCooler{Position: site.Cell, Rotation: site.Rotation}.Cold()
	dig := func() RoutineBuildingResult {
		t.Helper()
		var err error
		if s.goal, err = db.LoadGoal(ctx, s.goal.Goal.ID); err != nil {
			t.Fatal(err)
		}
		result, handled, err := p.digPlanned(ctx, ctx, s, []domain.Cell{shaft}, cold, "plan-dig-test", nil, func() error { return nil })
		if err != nil || !handled {
			t.Fatal(result, handled, err)
		}
		return result
	}
	first := dig()
	if first.Verdict != BuildingReasonAdmitted {
		t.Fatal(first.Verdict)
	}
	if again := dig(); again.Verdict != BuildingReasonUsed {
		t.Fatal("the open dig plan must be waited on, not doubled", again.Verdict)
	}
	completeRoutineBuildingMethod(t, db, first)
	stuck := dig()
	if !stuck.Verdict.Is(RefusalRockNotDug) || stuck.Verdict.Outcome != OutcomeRefused {
		t.Fatal(stuck.Verdict)
	}
	if text := stuck.Verdict.Text(); !strings.Contains(text, "still standing") || !strings.Contains(text, "plan dig test") {
		t.Fatal(text)
	}
}
