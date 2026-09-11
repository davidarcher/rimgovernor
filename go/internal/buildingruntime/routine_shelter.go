package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// NewRoutineShelterPlanner prefers furnishing verified indoor space. Only when
// that whole method has no space does it propose a native-grounded starter shell.
func NewRoutineShelterPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureInitialShelter, definition: "Wall", shelter: true}, nil
}

// A completed starter shell may trigger RimWorld's normal automatic roofing.
// Give that work at most one in-game hour from the durable completion tick.
// Polling, restarting or cancelling cannot renew this budget.
func shelterNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) != 32 {
		return 0
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	completed := domain.Tick(0)
	for _, p := range plan.Progress {
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || v.Tick > tick {
			return 0
		}
		completed = max(completed, v.Tick)
	}
	const budget domain.Tick = 2500
	if tick-completed >= budget {
		return 0
	}
	return uint32(budget - (tick - completed))
}

func routineDefinitionsAvailable(facts observation.ColonyProjection, names []string, shell bool) bool {
	for _, name := range names {
		available := false
		for _, def := range facts.Definitions {
			if def.Name != name {
				continue
			}
			ready, known := def.Available.Value()
			skill, skillKnown := def.ConstructionSkill.Value()
			available = known && ready && skillKnown && skill == 0
			if shell {
				size, known := def.Size.Value()
				available = available && known && size.Width == 1 && size.Height == 1
			}
		}
		if !available {
			return false
		}
	}
	return true
}

func (r *RoutineBuildingPlanner) previewShell(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	var cells []policy.SiteCell
	for _, c := range facts.Cells {
		indoors, indoorKnown := c.Indoors.Value()
		roof, roofKnown := c.Roofed.Value()
		if indoorKnown && !indoors && roofKnown && !roof {
			cells = append(cells, c)
		}
	}
	layouts, err := policy.StarterLayouts(policy.StarterRequest{Bounds: facts.Bounds, Anchor: facts.Center, Cells: cells, Protected: protected})
	if err != nil {
		return nil, policy.StockObservation{}, "", err
	}
	for candidate, layout := range layouts {
		room := layout.Room
		door := domain.Cell{X: room.X + room.Width/2, Z: room.Z}
		perimeter := []domain.Cell{door}
		for x := room.X; x < room.X+room.Width; x++ {
			for z := room.Z; z < room.Z+room.Height; z++ {
				cell := domain.Cell{X: x, Z: z}
				if cell != door && (x == room.X || x == room.X+room.Width-1 || z == room.Z || z == room.Z+room.Height-1) {
					perimeter = append(perimeter, cell)
				}
			}
		}
		stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
		var selected []policy.Preview
		for i, cell := range perimeter {
			if err := check(); err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			definition := "Wall"
			if i == 0 {
				definition = "Door"
			}
			building, err := domain.NewBuilding(definition, cell, domain.North, "WoodLog")
			if err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d-%d", snapshot.Plan, candidate, i)), building)
			if err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
			if err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			if err := check(); err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			v := preview.Preview
			if v.Action != action || !v.Snapshot.Matches(snapshot) || v.Tick != facts.Identity.Tick || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != facts.Identity.Tick {
				return nil, policy.StockObservation{}, "", ErrControl
			}
			stuff, known := v.MadeFromStuff.Value()
			if !known || !stuff {
				return nil, policy.StockObservation{}, BuildingMethodUnknown, nil
			}
			footprint, known := v.Footprint.Value()
			can, canKnown := v.CanPlace.Value()
			safe, safeKnown := v.SafeToPlace.Value()
			if !known || len(footprint) != 1 || footprint[0] != cell || !canKnown || !can || !safeKnown || !safe {
				break
			}
			if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			selected = append(selected, v)
		}
		if len(selected) == len(perimeter) {
			return selected, stock, "", nil
		}
	}
	return nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
}

// Every preview in a paused project must agree on each shared stock value.
func mergeRoutineStock(stock *policy.StockObservation, next policy.StockObservation, first bool) error {
	if len(next.Values) > 256 {
		return ErrControl
	}
	if first {
		stock.NativeConstruction = next.NativeConstruction
	} else {
		stock.NativeConstruction = stock.NativeConstruction && next.NativeConstruction
	}
	values := make(map[policy.Resource]domain.Fact[int64], len(stock.Values))
	for _, v := range stock.Values {
		values[v.Resource] = v.Available
	}
	seen := map[policy.Resource]bool{}
	for _, v := range next.Values {
		if seen[v.Resource] {
			return ErrControl
		}
		seen[v.Resource] = true
		if old, exists := values[v.Resource]; exists {
			if old != v.Available {
				return ErrControl
			}
		} else {
			stock.Values = append(stock.Values, v)
			values[v.Resource] = v.Available
		}
	}
	if len(stock.Values) > 256 {
		return ErrControl
	}
	return nil
}
