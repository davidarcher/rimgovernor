package buildingruntime

import (
	"context"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// NewRoundsShelterPlanner prefers furnishing verified indoor space. Only when
// that whole method has no space does it raise the layout plan's shelter room:
// the bunk rungs, then the ring through one reconcile.
func NewRoundsShelterPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsShelterPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainHousing, phase: policy.HousingShelter, definition: "Wall", shelter: true}, nil
}

// Expansion reuses the same furnishing and ring path to keep one spare indoor
// sleeping place beyond the observed population.
func NewRoundsExpansionPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsExpansionPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainHousing, phase: policy.HousingExpansion, definition: "Wall", shelter: true}, nil
}

// roomModule is the layout module this planner furnishes: a facility
// ladder's room role names it, the shelter and expansion planners raise the
// shelter.
func (r *RoundsBuildingPlanner) roomModule() (policy.PlannedRole, bool) {
	return policy.PlannedRoleOf(r.roomRole())
}

// roomRole is the room role a planner sites for: a facility
// ladder's own role, and Shelter for the shelter and expansion planners,
// which raise the colony's temporary starter room.
func (r *RoundsBuildingPlanner) roomRole() policy.RoomRole {
	if r.facility != nil {
		return r.facility.Role
	}
	return policy.RoomRoleShelter
}

// A completed starter shell may trigger RimWorld's normal automatic roofing.
// Give that work at most four in-game hours from the durable completion tick.
// Polling, restarting or cancelling cannot renew this budget. Shells vary in
// size by shape, so any nonempty fully completed plan qualifies. The budget
// is scoped to the world and plan revision the walls were completed in, not
// to the native order generation: an authority re-acquisition between the
// last dispatch and the review (a cancelled transport call) moves the
// generation on without touching the standing walls, and nothing else lends
// the clock the roof needs when the shell is the only work.
func shelterNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) == 0 {
		return 0
	}
	completed := domain.Tick(0)
	for _, p := range plan.Progress {
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !boundary.World(v.Snapshot, current) ||
			v.Snapshot.Plan != plan.Spec.ID() || v.Snapshot.Revision != plan.Spec.Revision() || v.Tick > tick {
			return 0
		}
		completed = max(completed, v.Tick)
	}
	const budget domain.Tick = 10000
	if tick-completed >= budget {
		return 0
	}
	return uint32(budget - (tick - completed))
}

// roundsDefinitionsAvailable reports whether every named definition can be
// raised by any colonist now (no construction skill needed); a shell piece
// also has to be a single cell.
func roundsDefinitionsAvailable(facts observation.ColonyProjection, names []string, shell bool) bool {
	return definitionsGate(facts, names, shell).IsZero()
}

// definitionsGate is the verdict roundsDefinitionsAvailable reads: the zero
// Verdict when every named definition is buildable, else the first thing
// missing, named.
func definitionsGate(facts observation.ColonyProjection, names []string, shell bool) Verdict {
	for _, name := range names {
		def, found := definitionRow(facts, name)
		if !found {
			return fieldUnavailable(name + "_definition")
		}
		if v := definitionAvailability(def); !v.IsZero() {
			return v
		}
		skill, known := def.ConstructionSkill.Value()
		if !known {
			return fieldUnavailable(name + "_construction_skill")
		}
		if skill != 0 {
			return refuse(RefusalNoWorker, "builder_for_"+name, fmt.Sprintf("construction_skill_%d", skill))
		}
		if shell {
			size, known := def.Size.Value()
			if !known {
				return fieldUnavailable(name + "_size")
			}
			if size.Width != 1 || size.Height != 1 {
				return noSpace(name + "_footprint")
			}
		}
	}
	return Verdict{}
}

func definitionRow(facts observation.ColonyProjection, name string) (observation.PlanningDefinition, bool) {
	for _, def := range facts.Definitions {
		if def.Name == name {
			return def, true
		}
	}
	return observation.PlanningDefinition{}, false
}

// definitionAvailability is the zero Verdict for a definition the game
// reports buildable, else the missing fact or the research it waits on.
func definitionAvailability(def observation.PlanningDefinition) Verdict {
	ready, known := def.Available.Value()
	if !known {
		return fieldUnavailable(def.Name + "_availability")
	}
	if ready {
		return Verdict{}
	}
	if len(def.Research) == 0 {
		return awaitingPlan(def.Name, "unavailable")
	}
	return researchWait(strings.Join(def.Research, "+"))
}

func positiveFact(f domain.Fact[bool]) bool {
	v, known := f.Value()
	return known && v
}

// shellBatchPreviewer is the batched preview a native source may offer: one
// call for every placement of a wave instead of one hop per cell.
type shellBatchPreviewer interface {
	PreviewBuildings(context.Context, []domain.Action, domain.GenerationSnapshot) ([]bridge.BuildingPreview, bridge.Result, error)
}

// facilityLadder reports whether the planner walks a facility ladder whose
// furniture stands in a planned room (comfort, workshop, hospital, sleeping,
// laboratory), as opposed to the initial shelter and expansion, whose ring is
// the deficit itself.
func (r *RoundsBuildingPlanner) facilityLadder() bool {
	switch r.concern {
	case policy.MaintainResource, policy.MaintainEquipment, policy.MaintainMedicalReserves, policy.EnsureResearch:
		return true
	}
	return r.phase == policy.HousingSleeping || r.phase == policy.ComfortRanked
}

// mergeRoundsStock folds one more preview's stock into a bundle's. Previews
// of one bundle are native calls made one after another, and an Available
// count can differ between them (it is net of the native blueprint census
// and moves as colonists haul); the bundle is funded from the lowest value
// any preview saw, and an unknown one leaves the resource unknown.
func mergeRoundsStock(stock *policy.StockObservation, next policy.StockObservation, first bool) error {
	if first {
		stock.NativeConstruction = next.NativeConstruction
	} else {
		stock.NativeConstruction = stock.NativeConstruction && next.NativeConstruction
	}
	index := make(map[policy.Resource]int, len(stock.Values))
	for i, v := range stock.Values {
		index[v.Resource] = i
	}
	seen := map[policy.Resource]bool{}
	for _, v := range next.Values {
		if seen[v.Resource] {
			return fmt.Errorf("%w: mergeRoundsStock: seen[v.Resource]", ErrControl)
		}
		seen[v.Resource] = true
		if i, exists := index[v.Resource]; exists {
			stock.Values[i].Available = lowerStock(stock.Values[i].Available, v.Available)
		} else {
			stock.Values = append(stock.Values, v)
			index[v.Resource] = len(stock.Values) - 1
		}
	}
	return nil
}

func lowerStock(a, b domain.Fact[int64]) domain.Fact[int64] {
	x, xk := a.Value()
	y, yk := b.Value()
	if !xk || !yk {
		return domain.Unknown[int64]()
	}
	return domain.Known(min(x, y))
}
