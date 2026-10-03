package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// The throne room (#1601, epic #1598): MaintainHousing's sleeping planner
// shells the planned room, places the title's throne and furnishes it to the
// title's impressiveness (policy.NextThroneStep, policy.ThroneRoomTargets).
// The layout review grows the room from the title's minimum area
// (policy.ReplanLayoutWithThrone). Assigning the throne to its holder is the
// seam below: no ThroneAssign action kind exists yet.

// RoyaltyNative is the optional native royalty read behind the throne room
// (bridge.Client.RoyaltyFacts). A reviewer whose native lacks it, or whose
// colony has no Royalty, owes no throne room. A nil result is Royalty not
// applicable.
type RoyaltyNative interface {
	RoyaltyFacts(ctx context.Context, identity *c.Identity, now int64) (*policy.RoyaltyFacts, error)
}

// reviewRoyalty reads the royalty facts onto the projection (the native
// client holds them for RoyaltyRefreshTicks), resolves the ladder's throne
// definitions from the review's catalog and remembers the read for the
// planners. A failed read leaves royalty unknown.
func (r *RoutineReviewer) reviewRoyalty(ctx context.Context, snapshot domain.GenerationSnapshot, reading *observation.RoutineReading) {
	projection := &reading.Projection
	native, ok := r.native.(RoyaltyNative)
	if !ok || !r.methodEnabled(policy.MaintainHousing) {
		return
	}
	facts, err := native.RoyaltyFacts(ctx, controlIdentity(snapshot), int64(projection.Identity.Tick))
	if err != nil {
		clockSchedulerLog("royalty read deferred: %v", err)
		return
	}
	if facts == nil {
		return
	}
	projection.Royalty = domain.Known(*facts)
	projection.AddDefinitions(reading.Frame, throneThings(*facts))
	r.census.rememberRoyalty(projection.Identity, projection.Royalty)
}

// throneThings is every throne definition the ladder names, sorted.
func throneThings(f policy.RoyaltyFacts) []string {
	var names []string
	for _, rung := range f.Ladder {
		for _, thing := range rung.ThroneThings {
			if !slices.Contains(names, thing) {
				names = append(names, thing)
			}
		}
	}
	slices.Sort(names)
	return names
}

// rememberedThrones is the throne definitions a planner's read names beside
// its own: the ladder's, from the review's royalty read.
func (r *RoutineReviewer) rememberedThrones() []string {
	if facts, ok := r.census.remembered().Value(); ok {
		return throneThings(facts)
	}
	return nil
}

// throneNeed is the throne room the colony owes: unknown royalty owes none.
func throneNeed(facts observation.ColonyProjection) (policy.ThroneNeed, bool) {
	royalty, known := facts.Royalty.Value()
	if !known {
		return policy.ThroneNeed{}, false
	}
	return policy.NextThroneNeed(royalty)
}

// throneStep is the projection's next throne step; none while the plan, the
// room census or the construction census is unknown.
func throneStep(facts observation.ColonyProjection) policy.ThroneStep {
	need, owed := throneNeed(facts)
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !owed || !pk || !rk || !ck || !census.Colony {
		return policy.ThroneStep{}
	}
	defs := make([]policy.ThroneDefinition, 0, len(need.Things))
	for _, d := range facts.Definitions {
		if slices.Contains(need.Things, d.Name) {
			defs = append(defs, policy.ThroneDefinition{Name: d.Name, Available: d.Available, Size: d.Size})
		}
	}
	return policy.NextThroneStep(plan, rooms, census.Buildings, need, defs)
}

// withThroneTargets adds the throne room's impressiveness target to the
// room quality targets, so the room upgrade and the beauty upgrade furnish
// it.
func withThroneTargets(facts observation.ColonyProjection, targets map[string]policy.RoomTarget) map[string]policy.RoomTarget {
	need, owed := throneNeed(facts)
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	if !owed || !pk || !rk {
		return targets
	}
	if targets == nil {
		targets = map[string]policy.RoomTarget{}
	}
	for id, t := range policy.ThroneRoomTargets(plan, rooms, need) {
		targets[id] = t
	}
	return targets
}

// throneMethod names a throne step's method: the shell once per room, the
// throne once per slot, per goal epoch.
func throneMethod(step policy.ThroneStep) domain.MethodID {
	in := step.Room.Interior
	if step.Kind == policy.ThronePlace {
		return domain.MethodID(fmt.Sprintf("throne-place-%d-%d-%s", in.X, in.Z, step.Piece.Slot))
	}
	return domain.MethodID(fmt.Sprintf("throne-shell-%d-%d", in.X, in.Z))
}

// stageThrone answers a due throne step: the shell through shellRoom, the
// throne through placePiece. Furnishing follows through the room upgrade.
func (r *RoutineSleepingUpkeepPlanner) stageThrone(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, step policy.ThroneStep) (RoutineBuildingResult, error) {
	clockSchedulerLog("%s: throne room %s for %s (%s)", goal.Goal.ID, step.Kind, step.Need.Holder, step.Need.Title)
	switch step.Kind {
	case policy.ThroneShell:
		return r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, throneMethod(step), "throne room")
	case policy.ThronePlace:
		return r.building.placePiece(call, epoch, state, review, goal, reading, step.Piece, throneMethod(step))
	case policy.ThroneAssign:
		throneAssignmentSeam(step)
	}
	return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
}

// throneAssignmentSeam is where the throne is assigned to its holder once
// policy.NextThroneStep reports policy.ThroneAssign. It acts on nothing:
// assignment needs a ThroneAssign action kind (#1601 awaits that decision),
// so MaintainHousing never owes the step.
func throneAssignmentSeam(policy.ThroneStep) {}
