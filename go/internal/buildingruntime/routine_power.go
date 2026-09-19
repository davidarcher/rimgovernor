package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func NewRoutinePowerPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureBasicPower}, nil
}

func (r *RoutineBuildingPlanner) selectPower(facts observation.ColonyProjection) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	planning := policy.DefaultPowerPlanning()
	planning.Generators = facts.GeneratorOptions()
	proposal, err := policy.SelectPowerMethod(facts.PowerPlanning, facts.Bounds, facts.Cells, nil, planning)
	if err != nil {
		return nil, "", err
	}
	switch proposal.Method {
	case policy.PowerUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.PowerNoMethod:
		return nil, BuildingMethodNoDeficit, nil
	case policy.PowerRouteBlocked:
		return nil, BuildingMethodNoSpace, nil
	case policy.PowerNoGenerator:
		// Every generator the family can compile is unavailable; when the
		// research they require is unfinished, that is what the goal waits
		// on, not a cheaper generator.
		if gate := policy.ResearchGate(generatorResearch(facts), facts.Facts.Research); gate != "" {
			return nil, researchWaitReason(gate), nil
		}
		return nil, RoutineBuildingReason(proposal.Method), nil
	case policy.PowerWaitOutput, policy.PowerWaitFuel, policy.PowerWaitRepair, policy.PowerWaitBlackout, policy.PowerWaitPlayer:
		return nil, RoutineBuildingReason(proposal.Method), nil
	}
	resolved := *r
	resolved.power = &proposal
	resolved.definition, resolved.environment = string(proposal.Method), policy.PlacementAnywhere
	if proposal.Method == policy.PowerGenerate {
		resolved.definition = proposal.Definition
	}
	return &resolved, "", nil
}

// generatorResearch lists, in policy.GeneratorDefinitions order, the native
// research every unavailable generator definition requires.
func generatorResearch(facts observation.ColonyProjection) []string {
	definitions := map[string]observation.PlanningDefinition{}
	for _, d := range facts.Definitions {
		definitions[d.Name] = d
	}
	seen := map[string]bool{}
	var required []string
	for _, name := range policy.GeneratorDefinitions {
		d, ok := definitions[name]
		if !ok {
			continue
		}
		if available, known := d.Available.Value(); !known || available {
			continue
		}
		for _, project := range d.Research {
			if !seen[project] {
				seen[project] = true
				required = append(required, project)
			}
		}
	}
	return required
}

// Existing native power may need ordinary hauling/refueling, but only a
// completed method in this direction can lend a non-renewable clock budget.
func powerOutputAllowance(ctx context.Context, journal *store.Store, goal domain.Goal, current domain.GenerationSnapshot, tick domain.Tick) (uint32, error) {
	methods, err := journal.LoadGoalMethods(ctx, goal.ID, goal.Epoch)
	if err != nil {
		return 0, err
	}
	var allowance uint32
	for _, m := range methods {
		plan, err := journal.LoadPlan(ctx, m.Plan)
		if err != nil {
			return 0, err
		}
		allowance = max(allowance, powerNativeWorkTicks(plan, current, tick))
	}
	return allowance, nil
}

// powerDefinition reports whether a completed building belongs to the power
// family: a conduit or any compilable generator definition.
func powerDefinition(name string) bool {
	if name == "PowerConduit" {
		return true
	}
	for _, g := range policy.GeneratorDefinitions {
		if g == name {
			return true
		}
	}
	return false
}

func powerNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) < 1 || len(plan.Progress) > 8 {
		return 0
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	var completed domain.Tick
	for _, p := range plan.Progress {
		b, ok := p.Action().Building()
		if !ok || !powerDefinition(b.Definition()) {
			return 0
		}
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || tick < v.Tick {
			return 0
		}
		completed = max(completed, v.Tick)
	}
	if tick-completed >= 10000 {
		return 0
	}
	return min(uint32(120), uint32(10000-(tick-completed)))
}

// Conduits may legally underlay occupied cells. Validate the exact observed
// route with native previews instead of treating occupied cells as free floor.
func (r *RoutineBuildingPlanner) previewPowerRoute(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.power == nil || r.power.Method != policy.PowerConnect || len(r.power.Cells) < 1 || len(r.power.Cells) > 8 {
		return nil, stock, "", ErrControl
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range protected {
		blocked[c] = true
	}
	var selected []policy.Preview
	for i, cell := range r.power.Cells {
		if blocked[cell] {
			return nil, stock, BuildingMethodExistingWork, nil
		}
		if err := check(); err != nil {
			return nil, stock, "", err
		}
		building, err := domain.NewBuilding("PowerConduit", cell, domain.North, "")
		if err != nil {
			return nil, stock, "", err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return nil, stock, "", err
		}
		preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, stock, "", err
		}
		if err = check(); err != nil {
			return nil, stock, "", err
		}
		p := preview.Preview
		if p.Action != action || !p.Snapshot.Matches(snapshot) || !p.Tick.FreshFor(facts.Identity.Tick) || !preview.Stock.Snapshot.Matches(snapshot) || !preview.Stock.Tick.FreshFor(facts.Identity.Tick) {
			return nil, stock, "", ErrControl
		}
		footprint, fk := p.Footprint.Value()
		made, mk := p.MadeFromStuff.Value()
		legal, lk := p.CanPlace.Value()
		safe, sk := p.SafeToPlace.Value()
		if !fk || !mk || !lk || !sk {
			return nil, stock, BuildingMethodUnknown, nil
		}
		if made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
			return nil, stock, BuildingMethodNoSpace, nil
		}
		selected = append(selected, p)
		if err = mergeRoutineStock(&stock, preview.Stock, i == 0); err != nil {
			return nil, stock, "", err
		}
	}
	return selected, stock, "", nil
}
