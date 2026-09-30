package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyAnimals(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.UpkeepAnimal] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "animals") {
		return domain.Unknown[[]policy.UpkeepAnimal]()
	}
	rows := []policy.UpkeepAnimal{}
	for _, a := range u.Animals {
		state := a.Pawn.AnimalState
		area := domain.Unknown[string]()
		if state.SupportsAllowedAreas != nil && !hasIssue(state.Issues, "allowed_area") {
			area = domain.Known(state.GetAllowedAreaId())
		}
		training := make([]policy.HusbandryTrainable, 0, len(state.GetTraining()))
		for _, entry := range state.GetTraining() {
			training = append(training, policy.HusbandryTrainable{Def: entry.GetDefName(), Available: optional(entry.Available), Learned: optional(entry.Learned)})
		}
		storage := make([]policy.AnimalFeedStorage, 0, len(a.ReachableStorage))
		for _, zone := range a.ReachableStorage {
			storage = append(storage, policy.AnimalFeedStorage{Zone: zone.GetZoneId(), Accepts: append([]string{}, zone.Accepts...)})
		}
		candidates := make([]domain.Cell, 0, len(a.StorageCandidates))
		for _, cell := range a.StorageCandidates {
			candidates = append(candidates, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		rows = append(rows, policy.UpkeepAnimal{SupportsAreas: optional(state.SupportsAllowedAreas), AllowedArea: area, ID: policy.PawnID(a.Pawn.Pawn.GetId()), Label: a.Pawn.Pawn.GetLabel(), Gender: state.GetGender(), Definition: policy.Resource(a.Pawn.Pawn.GetDefName()), RequiresPen: optional(a.RequiresPen), Contained: optional(state.Contained), Release: optional(state.Release), Slaughter: optional(state.Slaughter), Pen: domain.Known(state.GetPenId()), SuitablePen: domain.Known(a.GetSuitablePenId()), SafeToSlaughter: optional(state.SafeToSlaughter), SafeToRelease: optional(state.SafeToRelease), Herd: herdFacts(a), Training: training, ReachableBenches: append([]string{}, a.ReachableBenchIds...), ReachableStorage: storage, StorageCandidates: candidates})
		last := &rows[len(rows)-1]
		last.Care, last.Bonded = optional(state.MedicalCare), optional(state.Bonded)
		if state.Conditions != nil {
			last.Conditions, last.LifeThreatening = bridge.CareConditions(state.Conditions)
		}
	}
	return domain.Known(rows)
}

// colonyWildAnimals decodes the factionless census MaintainHerd tames from.
// A native section issue (bound exceeded, read failed) leaves it unknown.
func colonyWildAnimals(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.UpkeepAnimal] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "wild_animals") {
		return domain.Unknown[[]policy.UpkeepAnimal]()
	}
	rows := []policy.UpkeepAnimal{}
	for _, a := range u.WildAnimals {
		state := a.Pawn.AnimalState
		rows = append(rows, policy.UpkeepAnimal{ID: policy.PawnID(a.Pawn.Pawn.GetId()), Definition: policy.Resource(a.Pawn.Pawn.GetDefName()), RequiresPen: domain.Known(false), Contained: domain.Known(false), Release: domain.Known(false), Slaughter: domain.Known(false), Pen: domain.Known(""), SuitablePen: domain.Known(""), Tameable: optional(state.Tameable), Tame: optional(state.Tame), MinimumHandlingSkill: minimumHandling(state), Herd: herdFacts(a)})
	}
	return domain.Known(rows)
}

// herdFacts decodes the native herd sizing facts (#875); food-channel costs
// and products merge in later through mergeHerdFoodFacts.
func herdFacts(a *o.AnimalFeed) policy.HerdFacts {
	state := a.GetPawn().GetAnimalState()
	return policy.HerdFacts{AgeYears: optional(state.AgeYears), LifeExpectancy: optional(state.LifeExpectancyYears), ManhunterOnTameFail: optional(state.ManhunterOnTameFail), Sick: optional(state.Sick), Adult: optional(state.Adult), SlaughterBarred: optional(state.SlaughterBarred), Venerated: optional(state.Venerated), Predator: a.GetPawn().GetPredator()}
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

func minimumHandling(state *o.AnimalState) domain.Fact[int] {
	if state.MinimumHandlingSkill == nil {
		return domain.Unknown[int]()
	}
	return domain.Known(int(state.GetMinimumHandlingSkill()))
}
