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

// bedroomGate is the personal-share gate on in-place bedroom upgrades
// (#1840): charged steps begin at Reserves and must fit the owners' remaining
// share. stage is the review's colony stage.
func bedroomGate(facts observation.ColonyProjection, stage policy.ColonyStage) policy.RoomGate {
	gate := policy.RoomGate{Stage: stage, Shares: facts.PersonalShareOf, Items: facts.Facts.Items, BedPrice: facts.BedPrice}
	// A piece is priced in the stuff upgradeBedroom would build it in; the
	// item facts carry no row for a stuff-made def.
	gate.PiecePrice = func(def string) (float64, bool) {
		if facts.BedPrice == nil {
			return 0, false
		}
		return facts.BedPrice(policy.Resource(def), policy.Resource(pieceStuff(facts, def)))
	}
	// A suite is furnished with the bed the furnish step stages and the
	// template pieces the closer fills (#1841), priced in their stuffs.
	definitions := make([]policy.BenchDefinition, 0, len(facts.Definitions))
	for _, d := range facts.Definitions {
		definitions = append(definitions, policy.BenchDefinition{Name: d.Name, Available: d.Available})
	}
	if bed, method := policy.SleepingDefinition(facts.Shapes.Furniture, definitions, nil, false, true); method == policy.SleepingBuild {
		gate.SuiteBed, gate.SuiteBedStuff = policy.Resource(bed), policy.Resource(pieceStuff(facts, bed))
	}
	for _, def := range facts.Shapes.Furniture.BedroomUpgradePieces() {
		if v, known := facts.DefinitionAvailable(def).Value(); def != "" && known && v {
			gate.SuitePieces = append(gate.SuitePieces, def)
		}
	}
	return gate
}

// pieceStuff is the stuff a bedroom upgrade piece is built in: the allowed
// stocked one with the best rest effectiveness, else the ordinary placement
// stuff.
func pieceStuff(facts observation.ColonyProjection, def string) string {
	stuff := facts.BuildStuff(def)
	if d, found := facts.Definition(def); found {
		stock, _ := facts.Stock()
		if price, err := d.StuffChoice(observation.MaxRestEffectiveness, stock); err == nil {
			stuff = price.Stuff
		}
	}
	return stuff
}

// roomUpgrade is the next room quality upgrade (#814): one template piece
// for an owned bedroom below its target.
func roomUpgrade(facts observation.ColonyProjection, stage policy.ColonyStage) (policy.RoomUpgrade, bool) {
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
	return policy.NextRoomUpgrade(obs, upgradeTargets(facts, targets, stage), policy.FurnitureRooms(rooms, census, facts.Cells), available, bedroomGate(facts, stage))
}

// bedReplacement is the next bed replacement step (#829), read from the
// same census as roomUpgrade.
func bedReplacement(facts observation.ColonyProjection, stage policy.ColonyStage) (policy.BedReplacement, bool) {
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
	materials := policy.BedMaterials{Cost: map[policy.Resource]int64{}, Items: facts.Facts.Items, Gate: bedroomGate(facts, stage)}
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
	return policy.NextBedReplacement(obs, upgradeTargets(facts, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), stage), policy.FurnitureRooms(rooms, census, facts.Cells), available, materials)
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
	return policy.NextTitleFurniture(obs, policy.FurnitureRooms(rooms, census, facts.Cells), available)
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
	return policy.NextCompanionBed(obs, animals, policy.FurnitureRooms(rooms, census, facts.Cells), facts.Shapes.Furniture, spot, available)
}

// beautyUpgrade is the next beauty lever (#830): a plant pot or a
// prettier floor for a bedroom whose weakest stat is beauty.
func beautyUpgrade(facts observation.ColonyProjection, stage policy.ColonyStage) (policy.RoomUpgrade, bool) {
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
	return policy.NextBeautyUpgrade(obs, upgradeTargets(facts, withThroneTargets(facts, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness)), stage), policy.FurnitureRooms(rooms, census, facts.Cells), available, facts.Facts.Upkeep.Flooring, floors, bedroomGate(facts, stage))
}

// removeOldBed deconstructs a replaced bed, once per bed per Episode.
func (r *RoundsSleepingUpkeepPlanner) removeOldBed(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, rep policy.BedReplacement) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	bed := sha256.Sum256([]byte(rep.Bed))
	method := domain.MethodID(fmt.Sprintf("bedroom-replace-remove-%x", bed[:8]))
	if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "old_bed_removal")}, nil
	}
	// A real bed is packed, not deconstructed: the stored bed furnishes the
	// next bedroom or bed spot (packShellBed, reinstallStoredBed).
	if rep.Def == "Bed" || rep.Def == policy.SleepingCoupleBedDefinition {
		value, err := domain.NewMoveBuilding(rep.Bed, rep.Def, rep.Cell, domain.South)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewUninstallBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		result, _, err := r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
		return result, err
	}
	return r.building.retireBuilding(call, epoch, state, review, goal, reading.ColonyReading, rep.Bed, rep.Def, rep.Cell, "bedroom-replace-remove", "old_bed_removal")
}

// upgradeBedroom previews and admits one upgrade piece, once per room and
// slot per Episode.
func (r *RoundsSleepingUpkeepPlanner) upgradeBedroom(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, u policy.RoomUpgrade) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	facts := reading.Projection
	room := sha256.Sum256([]byte(u.Room))
	method := domain.MethodID(fmt.Sprintf("bedroom-upgrade-%s-%x", u.Slot, room[:8]))
	if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "bedroom_upgrade")}, nil
	}
	stuff := u.Stuff
	if stuff == "" {
		stuff = pieceStuff(facts, u.Def)
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
		return RoundsBuildingResult{}, err
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
			return RoundsBuildingResult{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		v := preview.Preview
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !ck || !can || !sk || !safe {
			continue
		}
		if err := mergeRoundsStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return RoundsBuildingResult{}, err
		}
		selected = append(selected, v)
	}
	if len(selected) == 0 {
		return RoundsBuildingResult{Verdict: noSpace("bedroom_upgrade_site")}, nil
	}
	return r.building.admitPreviews(call, epoch, roundsAdmission{state: state, review: review, owner: goal, facts: facts, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Shelter})
}
