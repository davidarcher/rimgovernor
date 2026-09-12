package policy

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

const (
	MaintainAnimalContainment GoalID = "MaintainAnimalContainment"
	MaintainAnimalFeed        GoalID = "MaintainAnimalFeed"
)

type UpkeepAnimal struct {
	ID                                         PawnID
	Definition                                 Resource
	RequiresPen, Contained, Release, Slaughter domain.Fact[bool]
	Pen, SuitablePen                           domain.Fact[string]
}
type AnimalUpkeepObservation struct {
	Animals       domain.Fact[[]UpkeepAnimal]
	Food          domain.Fact[FoodSupply]
	DirectedHerds []Resource
}
type AnimalUpkeepPolicy struct{ FeedMinimumDays, FeedTargetDays float64 }

func DefaultAnimalUpkeepPolicy() AnimalUpkeepPolicy { return AnimalUpkeepPolicy{2, 4} }

type AnimalUpkeepHistory struct {
	Containment bool
	Feed        []PawnID
}

func (h AnimalUpkeepHistory) Validate() error {
	if len(h.Feed) > 256 {
		return errors.New("animal feed history exceeds bound")
	}
	seen := map[PawnID]bool{}
	for _, id := range h.Feed {
		if !foodID(string(id)) || seen[id] {
			return errors.New("invalid animal feed history")
		}
		seen[id] = true
	}
	return nil
}

type AnimalFeedTarget struct {
	ID                                PawnID
	RunwayDays, Nutrition, TargetDays float64
}
type AnimalUpkeepReview struct {
	History     AnimalUpkeepHistory
	Containment domain.Fact[[]PawnID]
	Feed        domain.Fact[[]AnimalFeedTarget]
}

func ReviewAnimalUpkeep(v AnimalUpkeepObservation, previous AnimalUpkeepHistory, p AnimalUpkeepPolicy) (AnimalUpkeepReview, error) {
	r := AnimalUpkeepReview{History: AnimalUpkeepHistory{Containment: previous.Containment, Feed: append([]PawnID{}, previous.Feed...)}}
	invalid := errors.New("invalid animal upkeep facts or history")
	if !foodNumber(p.FeedMinimumDays) || !foodNumber(p.FeedTargetDays) || p.FeedTargetDays <= p.FeedMinimumDays || len(v.DirectedHerds) > 256 {
		return r, invalid
	}
	if err := previous.Validate(); err != nil {
		return r, err
	}
	active := map[PawnID]bool{}
	for _, id := range previous.Feed {
		active[id] = true
	}
	directed := map[Resource]bool{}
	for _, race := range v.DirectedHerds {
		if !validResource(race) || directed[race] {
			return r, invalid
		}
		directed[race] = true
	}
	animals, known := v.Animals.Value()
	if !known {
		return r, nil
	}
	if len(animals) > 256 {
		return r, invalid
	}
	seen := map[PawnID]bool{}
	containment := []PawnID{}
	eligible := []PawnID{}
	containmentKnown, feedKnown := true, true
	for _, animal := range animals {
		if !foodID(string(animal.ID)) || seen[animal.ID] || !validResource(animal.Definition) {
			return r, invalid
		}
		seen[animal.ID] = true
		pen, pk := animal.RequiresPen.Value()
		contained, ck := animal.Contained.Value()
		release, rk := animal.Release.Value()
		slaughter, sk := animal.Slaughter.Value()
		if !pk || pen && (!ck || !rk || !sk) {
			containmentKnown = false
		} else if pen && !contained && !release && !slaughter {
			containment = append(containment, animal.ID)
		}
		if !rk || !sk {
			feedKnown = false
		} else if !release && !slaughter && !directed[animal.Definition] {
			eligible = append(eligible, animal.ID)
		}
	}
	if containmentKnown {
		sort.Slice(containment, func(i, j int) bool { return containment[i] < containment[j] })
		r.Containment = domain.Known(containment)
		r.History.Containment = len(containment) > 0
	}
	if !feedKnown {
		return r, nil
	}
	if len(eligible) == 0 {
		r.Feed = domain.Known([]AnimalFeedTarget{})
		r.History.Feed = []PawnID{}
		return r, nil
	}
	supply, known := v.Food.Value()
	if !known {
		return r, nil
	}
	forecast, err := ForecastFood(supply, eligible)
	if err != nil {
		return r, nil
	}
	rows := map[PawnID]ConsumerFoodForecast{}
	for _, row := range forecast.Consumers {
		rows[row.ID] = row
	}
	targets := []AnimalFeedTarget{}
	next := []PawnID{}
	for _, id := range eligible {
		row, exists := rows[id]
		if !exists {
			return r, nil
		}
		threshold := p.FeedMinimumDays
		if active[id] {
			threshold = p.FeedTargetDays
		}
		if row.RunwayDays < threshold {
			missing := max(0, p.FeedTargetDays*row.NutritionPerDay-row.UsableNutrition)
			if !foodNumber(missing) {
				return r, invalid
			}
			targets = append(targets, AnimalFeedTarget{id, row.RunwayDays, missing, p.FeedTargetDays})
			next = append(next, id)
		}
	}
	sort.Slice(targets, func(i, j int) bool {
		if targets[i].RunwayDays != targets[j].RunwayDays {
			return targets[i].RunwayDays < targets[j].RunwayDays
		}
		return targets[i].ID < targets[j].ID
	})
	sort.Slice(next, func(i, j int) bool { return next[i] < next[j] })
	r.Feed = domain.Known(targets)
	r.History.Feed = next
	return r, nil
}
