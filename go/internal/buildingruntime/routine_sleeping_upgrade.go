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
	targets := policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness)
	for id, t := range policy.CommonRoomTargets(obs, tier, facts.Impressiveness) {
		if _, owned := targets[id]; !owned {
			targets[id] = t
		}
	}
	targets = withThroneTargets(facts, targets)
	available := func(def string) bool {
		v, known := facts.DefinitionAvailable(def).Value()
		return known && v
	}
	return policy.NextRoomUpgrade(obs, upgradeTargets(facts, targets), policy.TidyFurnitureRooms(rooms, census, facts.Cells), available)
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
	materials := policy.BedMaterials{Cost: map[policy.Resource]int64{}, Items: facts.Facts.Items}
	materials.Stock, _ = facts.Resources.Value()
	for _, d := range facts.Definitions {
		// A bed's stuff is the allowed stocked one with the best rest
		// effectiveness (#1731); a def with no rest effectiveness is no bed.
		price, err := d.StuffChoice(observation.MaxRestEffectiveness, materials.Stock)
		if err != nil || price.Stuff == "" {
			continue
		}
		for _, c := range price.Costs {
			if c.Resource == policy.Resource(price.Stuff) {
				materials.Cost[policy.Resource(d.Name)] = c.Count
			}
		}
	}
	return policy.NextBedReplacement(obs, upgradeTargets(facts, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness)), policy.TidyFurnitureRooms(rooms, census, facts.Cells), available, materials)
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

// companionBed is the next animal sleeping spot for a master's solo
// bedroom (#1633): none while the definition is unavailable or unsized.
func companionBed(facts observation.ColonyProjection) (policy.RoomUpgrade, bool) {
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	animals, ak := facts.Facts.AnimalUpkeep.Animals.Value()
	if !sk || !rk || !ck || !ak || !census.Colony {
		return policy.RoomUpgrade{}, false
	}
	d, found := animalContainmentDefinition(facts.Definitions, facts.Shapes.Furniture.AnimalSpot)
	size, sizeKnown := d.Size.Value()
	available, availableKnown := d.Available.Value()
	if !found || !sizeKnown || !availableKnown || size.Width < 1 || size.Height < 1 {
		return policy.RoomUpgrade{}, false
	}
	spot := policy.InteriorPieceDef{Def: d.Name, Size: domain.Cell{X: size.Width, Z: size.Height}}
	return policy.NextCompanionBed(obs, animals, policy.TidyFurnitureRooms(rooms, census, facts.Cells), facts.Shapes.Furniture, spot, available)
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
	return policy.NextBeautyUpgrade(obs, upgradeTargets(facts, withThroneTargets(facts, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness))), policy.TidyFurnitureRooms(rooms, census, facts.Cells), available, facts.Facts.Upkeep.Flooring, floors)
}

// removeOldBed deconstructs a replaced bed, once per bed per goal epoch.
func (r *RoutineSleepingUpkeepPlanner) removeOldBed(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, rep policy.BedReplacement) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	bed := sha256.Sum256([]byte(rep.Bed))
	method := domain.MethodID(fmt.Sprintf("bedroom-replace-remove-%x", bed[:8]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Verdict: BuildingReasonUsed}, nil
	}
	// A real bed is packed, not deconstructed: the stored bed furnishes the
	// next bedroom or bed spot (furnishFromShell, reinstallStoredBed).
	if rep.Def == "Bed" || rep.Def == policy.SleepingCoupleBedDefinition {
		value, err := domain.NewMoveBuilding(rep.Bed, rep.Def, rep.Cell, domain.South)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewUninstallBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		clockSchedulerLog("%s: bedroom %s: pack replaced bed %s for reuse", goal.Goal.ID, rep.Room, rep.Bed)
		result, _, err := r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
		return result, err
	}
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: removeOldBed: p.session.State() != state", ErrControl)
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
		return RoutineBuildingResult{Verdict: BuildingReasonUsed}, nil
	}
	stuff := u.Stuff
	if stuff == "" {
		stuff = facts.BuildStuff(u.Def)
		if d, found := facts.Definition(u.Def); found {
			stock, _ := facts.Stock()
			if price, err := d.StuffChoice(observation.MaxRestEffectiveness, stock); err == nil {
				stuff = price.Stuff
			}
		}
	}
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: upgradeBedroom: p.session.State() != state", ErrControl)
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
		return RoutineBuildingResult{Verdict: BuildingReasonNoSpace}, nil
	}
	clockSchedulerLog("%s: bedroom upgrade %s %s x%d (weakest %s)", goal.Goal.ID, u.Room, u.Def, len(selected), u.Weakest)
	return r.building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Shelter})
}
