package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoundsAnimalContainmentPlanner composes MaintainAnimalContainment's
// containment-method decision (policy.SelectAnimalContainmentMethod) with the
// build side of the reconciler: the barn, its pens (fence, gate, flap and the
// PenMarker that claims each pen) and the hospital ward (stageHerdRooms). It is
// self-contained the way RoundsFieldPlanner is, on purpose: the shared
// shelter/cooking/comfort switch is actively edited by parallel building-family
// slices, and this goal's action family needs none of its machinery.
type RoundsAnimalContainmentPlanner struct {
	reviewer *Rounder
	native   RoundsBuildingSource
	// building raises the barn, pens and ward (stageHerdRooms).
	building *RoundsBuildingPlanner
}
type RoundsAnimalContainmentResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsAnimalContainmentPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsAnimalContainmentPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsAnimalContainmentPlanner: reviewer == nil || native == nil", ErrControl)
	}
	building := &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainAnimalContainment}
	return &RoundsAnimalContainmentPlanner{reviewer: reviewer, native: native, building: building}, nil
}

// animalHandlerAvailable ports the enabled-Handling-worker prerequisite:
// dead/downed/drafted/mental-state pawns are already excluded from Available,
// so this only asks whether any remaining pawn has an enabled, prioritized
// Handling work assignment. Any unresolved availability or work fact makes the
// whole census unknown rather than silently skipping that pawn.
func animalHandlerAvailable(pawns []policy.WorkPawn) domain.Fact[bool] {
	known := true
	available := false
	for _, p := range pawns {
		avail, ak := p.Available.Value()
		if !ak {
			known = false
			continue
		}
		if !avail {
			continue
		}
		work, wk := p.Work.Value()
		if !wk {
			known = false
			continue
		}
		for _, w := range work {
			if w.Work == "Handling" && !w.Disabled && w.Priority > 0 {
				available = true
			}
		}
	}
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(available)
}

func animalContainmentDefinition(definitions []observation.PlanningDefinition, name string) (observation.PlanningDefinition, bool) {
	for _, d := range definitions {
		if d.Name == name {
			return d, true
		}
	}
	return observation.PlanningDefinition{}, false
}

// animalContainmentStuff ports enclosure_site's shared-material requirement:
// Fence and FenceGate must observe the same optional stuff, known or not.
func animalContainmentStuff(a, b observation.PlanningDefinition) (string, bool) {
	return observation.SharedStuff(a, b)
}

// shellSharedStuff is the one stuff a shell raised from a and b is built
// from. The tier ladder's wall stuff comes first (wood at Camp, stone blocks
// after), then the cheapest stuff the stock covers; only a colony stocking
// none falls back to the cheapest allowed whatever the stock. Ranking by
// market value alone picked Bioferrite, which no colony holds.
func shellSharedStuff(facts observation.ColonyProjection, a, b observation.PlanningDefinition) (string, bool) {
	if want := shellStyle(facts).WallStuff; want != "" && len(a.StuffOptions) > 0 && a.MakeableFrom(want) && b.MakeableFrom(want) {
		return want, true
	}
	if stock, known := facts.Stock(); known {
		if price, err := a.StuffChoice(observation.CheapestStuff, stock); err == nil && b.MakeableFrom(price.Stuff) {
			return price.Stuff, true
		}
	}
	return animalContainmentStuff(a, b)
}

// containmentWait is the refusal of a containment step that waits on the
// handler, the native pen or the pen it needs.
func containmentWait(reason policy.AnimalContainmentReason) Verdict {
	switch reason {
	case policy.ContainmentWaitingHandler:
		return noWorker("animal_handler")
	case policy.ContainmentWaitingNativePen:
		return awaitingPlan("native_pen", "delivery")
	case policy.ContainmentMarkerExhausted:
		return awaitingPlan("native_pen", "marker_placed")
	case policy.ContainmentNoPen:
		return awaitingPlan("pen", "planned")
	}
	return awaitingPlan("pen", string(reason))
}

func (r *RoundsAnimalContainmentPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsAnimalContainmentResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsAnimalContainmentResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsAnimalContainmentResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsAnimalContainmentResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainAnimalContainment)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if !workable {
		return RoundsAnimalContainmentResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsAnimalContainmentResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	facts := read.Projection
	animals, animalsKnown := facts.Facts.AnimalUpkeep.Animals.Value()
	if !animalsKnown {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("animal_upkeep")}, nil
	}
	workPawns, workKnown := facts.WorkPawns.Value()
	if !workKnown {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("work_pawns")}, nil
	}
	handlerAvailable := animalHandlerAvailable(workPawns)
	if _, known := handlerAvailable.Value(); !known {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("animal_handler")}, nil
	}
	// The barn, its pens and the hospital ward: the ring, flap, beds, fence,
	// gate and marker, one reconcile at a time.
	built, err := r.stageHerdRooms(call, epoch, state, review, goal, expected, claims)
	if err != nil || built.Verdict != BuildingReasonNoDeficit {
		return built, err
	}
	choice, err := policy.SelectAnimalContainmentMethod(animals, handlerAvailable, pensStand(facts))
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if choice.Reason == policy.ContainmentNoDeficit {
		return RoundsAnimalContainmentResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	return RoundsAnimalContainmentResult{Verdict: containmentWait(choice.Reason)}, nil
}

// pensStand is policy.PensStand over the projection's plan, ground and
// construction census; false while any is unknown.
func pensStand(facts observation.ColonyProjection) bool {
	plan, pk := facts.LayoutPlan.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	ground, gk := colonyGround(facts)
	if !pk || !ck || !gk {
		return false
	}
	return policy.PensStand(plan, plan.GroundWithRock(ground, naturalRock(facts)), census.Buildings)
}
