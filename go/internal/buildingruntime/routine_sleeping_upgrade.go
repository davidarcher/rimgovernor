package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// roomUpgrade is the next room quality upgrade (#814): one template piece
// for an owned bedroom below its target.
func roomUpgrade(facts observation.ColonyProjection) (policy.RoomUpgrade, bool) {
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !sk || !rk || !ck || !census.Colony || traits == nil {
		return policy.RoomUpgrade{}, false
	}
	tier, _ := facts.BuildTier.Value()
	targets := policy.RoomQualityTargets(obs, traits, tier)
	for id, t := range policy.CommonRoomTargets(obs, tier) {
		if _, owned := targets[id]; !owned {
			targets[id] = t
		}
	}
	available := func(def string) bool {
		v, known := facts.DefinitionAvailable(def).Value()
		return known && v
	}
	return policy.NextRoomUpgrade(obs, targets, policy.TidyFurnitureRooms(rooms, census, facts.Cells), available)
}

// bedReplacement is the next bed replacement step (#829), read from the
// same census as roomUpgrade.
func bedReplacement(facts observation.ColonyProjection) (policy.BedReplacement, bool) {
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !sk || !rk || !ck || !census.Colony || traits == nil {
		return policy.BedReplacement{}, false
	}
	tier, _ := facts.BuildTier.Value()
	available := func(def string) bool {
		v, known := facts.DefinitionAvailable(def).Value()
		return known && v
	}
	materials := policy.BedMaterials{Cost: map[policy.Resource]int64{}}
	materials.Stock, _ = facts.Resources.Value()
	for _, d := range facts.Definitions {
		stuff, sk := d.Stuff.Value()
		costs, ck := d.Costs.Value()
		for _, c := range costs {
			if sk && ck && c.Resource == policy.Resource(stuff) {
				materials.Cost[policy.Resource(d.Name)] = c.Count
			}
		}
	}
	return policy.NextBedReplacement(obs, policy.RoomQualityTargets(obs, traits, tier), policy.TidyFurnitureRooms(rooms, census, facts.Cells), available, materials)
}

// titleFurniture is the next unmet royal bedroom thing (#815).
func titleFurniture(facts observation.ColonyProjection) (policy.RoomUpgrade, bool) {
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !sk || !rk || !ck || !census.Colony {
		return policy.RoomUpgrade{}, false
	}
	available := func(def string) bool {
		v, known := facts.DefinitionAvailable(def).Value()
		return known && v
	}
	return policy.NextTitleFurniture(obs, policy.TidyFurnitureRooms(rooms, census, facts.Cells), available)
}

// beautyUpgrade is the next beauty lever (#830): a plant pot or a
// prettier floor for a bedroom whose weakest stat is beauty.
func beautyUpgrade(facts observation.ColonyProjection) (policy.RoomUpgrade, bool) {
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !sk || !rk || !ck || !census.Colony || traits == nil {
		return policy.RoomUpgrade{}, false
	}
	tier, _ := facts.BuildTier.Value()
	available := func(def string) bool {
		v, known := facts.DefinitionAvailable(def).Value()
		return known && v
	}
	floors := policy.FlooringFacts{Definitions: map[string]policy.FloorDefinition{}, Stock: facts.Resources}
	for _, d := range facts.Definitions {
		floors.Definitions[d.Name] = policy.FloorDefinition{Available: d.Available, Terrain: d.Terrain, Cleanliness: d.Cleanliness, Beauty: d.Beauty, Flammability: d.Flammability, PathCost: d.PathCost, Costs: d.Costs, WorkToBuild: d.WorkToBuild}
	}
	return policy.NextBeautyUpgrade(obs, policy.RoomQualityTargets(obs, traits, tier), policy.TidyFurnitureRooms(rooms, census, facts.Cells), available, facts.Facts.Upkeep.Flooring, floors)
}

// removeOldBed deconstructs a replaced bed, once per bed per goal epoch.
func (r *RoutineSleepingUpkeepPlanner) removeOldBed(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, rep policy.BedReplacement) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	bed := sha256.Sum256([]byte(rep.Bed))
	method := domain.MethodID(fmt.Sprintf("bedroom-replace-remove-%x", bed[:8]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	snapshot := state.Snapshot
	snapshot.Plan = domain.PlanID(fmt.Sprintf("routine-sleeping-replace-%x", digest[:16]))
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return ErrControl
		}
		return nil
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	value, err := domain.NewDeconstruction(rep.Bed, rep.Def, rep.Cell)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	action, err := domain.NewDeconstructionAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), value)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	clockSchedulerLog("%s: bedroom %s: remove replaced bed %s", goal.Goal.ID, rep.Room, rep.Bed)
	facts := reading.Projection
	return r.building.admitExcavation(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading.ColonyReading}, snapshot, method, plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}, check)
}

// upgradeBedroom previews and admits one upgrade piece, once per room and
// slot per goal epoch.
func (r *RoutineSleepingUpkeepPlanner) upgradeBedroom(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, u policy.RoomUpgrade) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	facts := reading.Projection
	room := sha256.Sum256([]byte(u.Room))
	method := domain.MethodID(fmt.Sprintf("bedroom-upgrade-%s-%x", u.Slot, room[:8]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	stuff := u.Stuff
	for _, d := range facts.Definitions {
		if stuff == "" && d.Name == u.Def {
			stuff, _ = d.Stuff.Value()
		}
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	snapshot := state.Snapshot
	snapshot.Plan = domain.PlanID(fmt.Sprintf("routine-sleeping-upgrade-%x", digest[:16]))
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return ErrControl
		}
		return nil
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	cells := u.Cells
	if len(cells) == 0 {
		cells = []domain.Cell{u.Anchor}
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	var selected []policy.Preview
	for i, cell := range cells {
		building, err := domain.NewBuilding(u.Def, cell, u.Rot, stuff)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		v := preview.Preview
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !ck || !can || !sk || !safe {
			clockSchedulerLog("%s: bedroom upgrade %s %s refused at %d,%d", goal.Goal.ID, u.Room, u.Def, cell.X, cell.Z)
			continue
		}
		if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return RoutineBuildingResult{}, err
		}
		selected = append(selected, v)
	}
	if len(selected) == 0 {
		return RoutineBuildingResult{Reason: BuildingMethodNoSpace}, nil
	}
	clockSchedulerLog("%s: bedroom upgrade %s %s x%d (weakest %s)", goal.Goal.ID, u.Room, u.Def, len(selected), u.Weakest)
	return r.building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Shelter})
}
