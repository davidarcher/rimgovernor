package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The throne room (#1601, epic #1598): MaintainHousing's sleeping planner
// shells the planned room, places the title's throne and furnishes it to the
// title's impressiveness (policy.NextThroneStep, policy.ThroneRoomTargets).
// The layout review grows the room from the title's minimum area
// (policy.ReplanLayoutWithRooms). Once it stands, the throne is assigned to
// its holder through the generic Assign action; the step holds
// MaintainHousing open until the royalty read lists the holder as its owner.

// reviewRoyalty reads the royalty facts onto the projection, resolves the
// ladder's throne definitions from the review's catalog and remembers the
// read for the planners. A colony without Royalty owes no throne room; a
// failed read leaves royalty unknown.
func (r *Rounder) reviewRoyalty(ctx context.Context, snapshot domain.GenerationSnapshot, reading *observation.RoundsReading) error {
	projection := &reading.Projection
	if !r.methodEnabled(policy.MaintainHousing) && !r.methodEnabled(policy.MaintainPsylink) && !r.moodCasts {
		return nil
	}
	// The colony section gates Royalty; the ladder and permits come from the
	// def mirror (#1861, #1875) and the colonists' own holdings from their
	// pawn rows (#1876). A read that cannot be used leaves royalty unknown.
	royalty, err := projection.RoyaltyOf(reading.Frame.Pawns, reading.Frame.Catalog)
	if err != nil {
		clockSchedulerLog("royalty read deferred: %v", err)
		return nil
	}
	facts, known := royalty.Value()
	if !known {
		return nil
	}
	projection.Royalty = royalty
	if err := projection.AddDefinitions(reading.Frame, append(throneThings(facts), throneFloorTerrains(reading.Frame.Catalog, facts)...)); err != nil {
		return err
	}
	r.census.rememberRoyalty(projection.Identity, projection.Royalty)
	return nil
}

// throneThings is every throne definition the ladder names, sorted.
func throneThings(f policy.RoyaltyFacts) []string {
	var names []string
	for _, rung := range f.Ladder {
		req, _ := rung.Throne.Value()
		for _, thing := range req.Things {
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
func (r *Rounder) rememberedThrones() []string {
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
	if need, ok := policy.CeremonyThroneNeed(royalty); ok {
		return need, true
	}
	return policy.NextThroneNeed(royalty)
}

// titleClaimQuests are the bestowing-ceremony quests the title claim gate
// allows to accept now (policy.NextTitleClaim, policy.ClaimQuests); none
// while the royalty read, sleeping census, plan, rooms or construction
// census is unknown.
func titleClaimQuests(facts observation.ColonyProjection) []domain.QuestID {
	royalty, rok := facts.Royalty.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !rok || !sk || !pk || !rk || !ck || !census.Colony {
		return nil
	}
	impressiveness := map[string]float64{}
	if upkeep, ok := sleeping.Rooms.Value(); ok {
		for _, room := range upkeep {
			if q, ok := room.Quality.Value(); ok {
				impressiveness[room.ID] = float64(q.Impressiveness)
			}
		}
	}
	claim := policy.NextTitleClaim(policy.TitleClaimFacts{Royalty: royalty, Sleeping: sleeping, BedroomPieces: policy.TidyFurnitureRooms(rooms, census, facts.Cells),
		Plan: plan, Rooms: rooms, Built: census.Buildings, Impressiveness: impressiveness})
	return policy.ClaimQuests(royalty, claim)
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
	names := need.DefNames()
	defs := make([]policy.FurnitureDefinition, 0, len(names))
	for _, d := range facts.Definitions {
		if slices.Contains(names, d.Name) {
			defs = append(defs, policy.FurnitureDefinition{Name: d.Name, Available: d.Available, Size: d.Size})
		}
	}
	royalty, _ := facts.Royalty.Value()
	step := policy.NextThroneStep(plan, rooms, census.Buildings, need, defs, royalty.Thrones)
	if step.Kind != policy.ThroneNone {
		return step
	}
	// Everything stands and is assigned: keep its braziers lit.
	lighting, lk := facts.Facts.Upkeep.Lighting.Value()
	workers, wk := facts.WorkPawns.Value()
	if !lk || !wk {
		return step
	}
	return policy.NextThroneRefuel(plan, rooms, need, lighting.Lamps, workers)
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

// withThroneFloor marks the standing throne room in the flooring census with
// the title's flooring requirement (#1863), so the flooring review measures
// it against the required terrain tags. The census rooms are copied: the
// projection's own stay untouched. No requirement, no standing room or an
// unlisted room leaves the census as read.
func withThroneFloor(facts observation.ColonyProjection, v policy.FlooringObservation) policy.FlooringObservation {
	need, owed := throneNeed(facts)
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	if !owed || !pk || !rk || len(need.FloorTags) == 0 {
		return v
	}
	room, ok := plan.ThroneRoomFor(need.MinArea)
	if !ok {
		return v
	}
	standing, ok := policy.PlannedRoomStanding(room, rooms)
	if !ok {
		return v
	}
	v.Rooms = slices.Clone(v.Rooms)
	for i := range v.Rooms {
		if v.Rooms[i].ID == standing.ID {
			v.Rooms[i].RequiredTags, v.Rooms[i].RequiredLabel = need.FloorTags, need.FloorLabel
		}
	}
	return v
}

// throneFloorTerrains is every terrain the ladder's titles accept as a throne
// floor, from the mirror's terrain tags: the definitions the flooring read
// must carry for the throne tier to choose among them.
func throneFloorTerrains(catalog *bridge.DefinitionCatalog, f policy.RoyaltyFacts) []string {
	var tags []string
	for _, rung := range f.Ladder {
		req, _ := rung.Throne.Value()
		tags = append(tags, req.FloorTags...)
	}
	return catalog.TerrainsWithTags(tags)
}

// throneMethod names a throne step's method: the shell once per room, the
// throne once per slot, per Episode.
func throneMethod(step policy.ThroneStep) domain.MethodID {
	in := step.Room.Interior
	if step.Kind == policy.ThronePlace {
		return domain.MethodID(fmt.Sprintf("throne-place-%d-%d-%s", in.X, in.Z, step.Piece.Slot))
	}
	return domain.MethodID(fmt.Sprintf("throne-shell-%d-%d", in.X, in.Z))
}

// stageThrone answers a due throne step: the shell through shellRoom, the
// throne through placePiece. Furnishing follows through the room upgrade.
func (r *RoundsSleepingUpkeepPlanner) stageThrone(call, epoch context.Context, arbiter *stepArbiter, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, step policy.ThroneStep) (RoundsBuildingResult, error) {
	clockSchedulerLog("%s: throne room %s for %s (%s)", goal.OwnerID(), step.Kind, step.Need.Holder, step.Need.Title)
	switch step.Kind {
	case policy.ThroneShell:
		return r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, throneMethod(step), "throne room")
	case policy.ThronePlace:
		return r.building.placePiece(call, epoch, state, review, goal, reading, step.Piece, throneMethod(step))
	case policy.ThroneRefuel:
		return r.refuelThrone(call, epoch, arbiter, state, review, goal, reading, step)
	case policy.ThroneAssign:
		return r.assignThrone(call, epoch, state, review, goal, step)
	}
	return RoundsBuildingResult{Verdict: fieldUnavailable("throne_step")}, nil
}

// assignThrone commits one Assign of the standing throne to its holder,
// once per holder and throne per Episode (refused attempts retry within
// assignMethod's bound). The royalty read refreshes on its own cadence, so a
// throne assigned this epoch reads unowned until then and the method is used.
func (r *RoundsSleepingUpkeepPlanner) assignThrone(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, step policy.ThroneStep) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	method, err := r.assignMethod(call, goal, fmt.Sprintf("throne-assign-%s-%s", step.Need.Holder, step.Throne))
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if method == "" {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "throne_assignment")}, nil
	}
	previous := domain.ClearPrevious()
	if step.PreviousThrone != "" {
		if previous, err = domain.KnownPrevious(step.PreviousThrone); err != nil {
			return RoundsBuildingResult{}, err
		}
	}
	assign, err := domain.NewAssign(domain.PawnID(step.Need.Holder), step.Throne, previous)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewAssignAction(domain.ActionID(fmt.Sprintf("%s-0", id)), assign)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsBuildingResult{}, err
	}
	if p.session.State() != state {
		return RoundsBuildingResult{}, fmt.Errorf("%w: assignThrone: p.session.State() != state", ErrControl)
	}
	latest, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if latest.Revision != review.Revision || !latest.Enabled {
		return RoundsBuildingResult{}, fmt.Errorf("%w: assignThrone: latest.Revision != review.Revision || !latest.Enabled", ErrControl)
	}
	if err = p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBuildingResult{}, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}
