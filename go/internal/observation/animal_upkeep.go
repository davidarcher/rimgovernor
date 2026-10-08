package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// colonyAnimals decodes the player herd, each animal joined to its pawn
// table row (#1343); an animal the table does not hold, or holds without
// its animal state, leaves the herd unknown until a later frame.
func colonyAnimals(v *o.ColonyFactsSnapshot, pawns bridge.Pawns, races policy.AnimalRaceCatalog) domain.Fact[[]policy.UpkeepAnimal] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "animals") {
		return domain.Unknown[[]policy.UpkeepAnimal]()
	}
	resolved, ok := animalRows(u.Animals, pawns)
	if !ok {
		return domain.Unknown[[]policy.UpkeepAnimal]()
	}
	rows := []policy.UpkeepAnimal{}
	for i, a := range u.Animals {
		pawn := resolved[i]
		state := pawn.AnimalState
		area := domain.Unknown[string]()
		if state.SupportsAllowedAreas != nil && !hasIssue(state.Issues, "allowed_area") {
			area = domain.Known(state.GetAllowedAreaId())
		}
		training := make([]policy.HusbandryTrainable, 0, len(state.GetTraining()))
		for _, entry := range state.GetTraining() {
			training = append(training, policy.HusbandryTrainable{Def: entry.GetDefName(), Available: optional(entry.Available), Learned: optional(entry.Learned), Wanted: optional(entry.Wanted)})
		}
		rows = append(rows, policy.UpkeepAnimal{SupportsAreas: optional(state.SupportsAllowedAreas), AllowedArea: area, ID: policy.PawnID(pawn.Pawn.GetId()), Label: pawn.Pawn.GetLabel(), Gender: state.GetGender(), Definition: policy.Resource(pawn.Pawn.GetDefName()), RequiresPen: optional(a.RequiresPen), Contained: optional(state.Contained), Release: optional(state.Release), Slaughter: optional(state.Slaughter), Pen: domain.Known(state.GetPenId()), SuitablePen: domain.Known(a.GetSuitablePen().GetId()), SlaughterFacts: policy.SlaughterFacts{Downed: optional(state.Downed), InMentalState: optional(state.InMentalState), Pregnant: optional(state.Pregnant), Mastered: mastered(state), ColonistBonded: optional(state.ColonistBonded), Designatable: optional(state.SlaughterDesignatable)}, SafeToRelease: optional(state.SafeToRelease), Herd: herdFacts(pawn, races), Training: training})
		last := &rows[len(rows)-1]
		last.Care, last.Bonded, last.BondedPawns = careName(state.MedicalCare), optional(state.Bonded), state.GetBondedPawnIds()
		last.Master, last.FollowDrafted, last.FollowFieldwork, last.Obedient = optional(state.MasterId), optional(state.FollowDrafted), optional(state.FollowFieldwork), optional(state.Obedient)
		last.Sterilized, last.SterilizeQueued = optional(state.Sterilized), optional(state.SterilizeQueued)
		if state.Conditions != nil {
			last.Conditions, last.LifeThreatening = bridge.CareConditions(state.Conditions)
		}
	}
	return domain.Known(rows)
}

// colonyWildAnimals decodes the factionless census MaintainHerd tames from.
// A native section issue (bound exceeded, read failed) leaves it unknown.
func colonyWildAnimals(v *o.ColonyFactsSnapshot, pawns bridge.Pawns, races policy.AnimalRaceCatalog) domain.Fact[[]policy.UpkeepAnimal] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "wild_animals") {
		return domain.Unknown[[]policy.UpkeepAnimal]()
	}
	resolved, ok := animalRows(u.WildAnimals, pawns)
	if !ok {
		return domain.Unknown[[]policy.UpkeepAnimal]()
	}
	rows := []policy.UpkeepAnimal{}
	for _, pawn := range resolved {
		state := pawn.AnimalState
		rows = append(rows, policy.UpkeepAnimal{ID: policy.PawnID(pawn.Pawn.GetId()), Definition: policy.Resource(pawn.Pawn.GetDefName()), RequiresPen: domain.Known(false), Contained: domain.Known(false), Release: domain.Known(false), Slaughter: domain.Known(false), Pen: domain.Known(""), SuitablePen: domain.Known(""), Gender: state.GetGender(), Tameable: optional(state.Tameable), Tame: optional(state.Tame), MinimumHandlingSkill: raceOf(pawn, races).MinimumHandlingSkill, Pest: raceOf(pawn, races).Pest, Herd: herdFacts(pawn, races)})
	}
	return domain.Known(rows)
}

// animalRows resolves each feed row's animal against the pawn table, false
// when one is missing or carries no animal state.
func animalRows(feed []*o.AnimalFeed, pawns bridge.Pawns) ([]*o.PawnState, bool) {
	out := make([]*o.PawnState, len(feed))
	for i, a := range feed {
		row, ok := pawns.Row(a.GetPawn())
		if !ok || row.AnimalState == nil {
			return nil, false
		}
		out[i] = row
	}
	return out, true
}

// raceOf is the catalog row of the pawn's race; a race the catalog does not
// hold is the zero row, whose numbers are unknown.
func raceOf(pawn *o.PawnState, races policy.AnimalRaceCatalog) policy.AnimalRace {
	race, _ := races.Race(policy.Resource(pawn.GetPawn().GetDefName()))
	return race
}

// herdFacts decodes the native herd sizing facts (#875) with the race's own
// numbers from the catalog's race row (#1722); food-channel costs and
// products merge in later through mergeHerdFoodFacts.
func herdFacts(pawn *o.PawnState, races policy.AnimalRaceCatalog) policy.HerdFacts {
	state := pawn.GetAnimalState()
	race := raceOf(pawn, races)
	return policy.HerdFacts{AgeYears: optional(state.AgeYears), LifeExpectancy: race.LifeExpectancy, ManhunterOnTameFail: race.ManhunterOnTameFail, Sick: optional(state.Sick), Adult: optional(state.Adult), Venerated: optional(state.Venerated), Predator: race.Predator,
		TicksToBirth: optional(state.TicksToBirth), LifeStageIndex: optional(state.LifeStageIndex), TicksToNextLifeStage: optional(state.TicksToNextLifeStage)}
}

// mergeHerdFoodFacts copies each player animal's meat, grazing demand and
// product flag from the colony food channels, matched by animal ID.
func mergeHerdFoodFacts(animals domain.Fact[[]policy.UpkeepAnimal], channels domain.Fact[FoodChannels]) domain.Fact[[]policy.UpkeepAnimal] {
	rows, rk := animals.Value()
	food, fk := channels.Value()
	if !rk || !fk {
		return animals
	}
	slaughter := map[policy.PawnID]policy.SlaughterFoodAnimal{}
	for _, s := range food.Slaughter {
		slaughter[s.ID] = s
	}
	product := map[policy.PawnID]bool{}
	for _, g := range food.Gatherable {
		if _, known := g.Resource.Value(); known {
			product[policy.PawnID(g.PawnID)] = true
		}
	}
	for _, e := range food.EggLayer {
		product[policy.PawnID(e.PawnID)] = true
	}
	out := make([]policy.UpkeepAnimal, len(rows))
	for i, a := range rows {
		if s, ok := slaughter[a.ID]; ok {
			a.Herd.MeatNutrition, a.Herd.FeedPerDay = s.MeatNutrition, s.FeedPerDay
		}
		a.Herd.Product = product[a.ID]
		out[i] = a
	}
	return domain.Known(out)
}

// mastered is whether the animal has a master; unknown until native sends the id.
func mastered(state *o.AnimalState) domain.Fact[bool] {
	if state.MasterId == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(state.GetMasterId() != "")
}
