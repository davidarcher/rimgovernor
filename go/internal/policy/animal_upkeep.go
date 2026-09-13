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
	// SafeToSlaughter and Training carry MaintainHerd-*'s husbandry
	// eligibility straight from the same generic census read that already
	// decodes containment/feed facts (AnimalState already exposes both), so
	// no dedicated per-cycle husbandry read is needed to detect the deficit.
	SafeToSlaughter domain.Fact[bool]
	Training        []HusbandryTrainable
}

// HusbandryTrainable is one trainable definition's recursive-training
// eligibility for one animal, ported from AnimalState.TrainingEntry.
type HusbandryTrainable struct {
	Def              string
	Available, Learned domain.Fact[bool]
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

type AnimalContainmentReason string

const (
	// ContainmentNoDeficit mirrors an empty animal_upkeep.containment_method row set.
	ContainmentNoDeficit AnimalContainmentReason = "no_uncontained_animal"
	// ContainmentWaitingHandler ports the enabled-Handling-worker prerequisite:
	// every uncontained animal already has a suitable pen, so nothing is built,
	// but native delivery still needs an available handler to walk it there.
	ContainmentWaitingHandler AnimalContainmentReason = "no_enabled_available_handler"
	// ContainmentWaitingNativePen is the ordinary wait once a handler exists;
	// native AI, not the controller, delivers an already-suitable-penned animal.
	ContainmentWaitingNativePen AnimalContainmentReason = "waiting_for_native_pen_delivery"
	// ContainmentExceedsBound ports the explicit >8 bounded admission refusal.
	ContainmentExceedsBound  AnimalContainmentReason = "herd_exceeds_bounded_pen_admission"
	ContainmentBuildShell    AnimalContainmentReason = "build_pen_shell"
	ContainmentAwaitingShell AnimalContainmentReason = "awaiting_shell_completion"
	ContainmentPlaceMarker   AnimalContainmentReason = "place_pen_marker"
	// ContainmentMarkerExhausted mirrors the Python SkillBlocked once a marker
	// method was already attempted with no observed suitable enclosure yet.
	ContainmentMarkerExhausted AnimalContainmentReason = "marker_placed_awaiting_native_pen"
)

// AnimalContainmentShellStage retains the durable shell staging animal_upkeep.
// containment_method reads off prior plan steps: nothing attempted yet, a shell
// proposed but not yet observed complete, or one already completed. Only a
// completed shell may ever receive a marker, and only once per shell.
type AnimalContainmentShellStage int

const (
	ContainmentShellNone AnimalContainmentShellStage = iota
	ContainmentShellPending
	ContainmentShellComplete
)

type AnimalContainmentMethod struct {
	Reason  AnimalContainmentReason
	Animals []PawnID
}

const maxAnimalContainmentHerd = 8

// SelectAnimalContainmentMethod ports animal_upkeep.containment_method: an
// already-suitable herd only ever waits for an enabled Handling worker and
// then native delivery; construction is bounded to a small starting herd and
// proceeds shell-then-marker, never duplicating a completed shell or retrying
// an attempted marker without a fresh observation. Native pen eligibility,
// footprint legality and construction admission remain the building family's.
func SelectAnimalContainmentMethod(animals []UpkeepAnimal, handlerAvailable domain.Fact[bool], shell AnimalContainmentShellStage, markerAttempted bool) (AnimalContainmentMethod, error) {
	if len(animals) > 256 {
		return AnimalContainmentMethod{}, errors.New("animal containment census exceeds bound")
	}
	seen := map[PawnID]bool{}
	var rows []UpkeepAnimal
	allSuitable := true
	for _, a := range animals {
		if !foodID(string(a.ID)) || seen[a.ID] {
			return AnimalContainmentMethod{}, errors.New("invalid animal containment census")
		}
		seen[a.ID] = true
		pen, pk := a.RequiresPen.Value()
		if !pk {
			return AnimalContainmentMethod{}, errors.New("animal pen requirement unknown")
		}
		if !pen {
			continue
		}
		contained, ck := a.Contained.Value()
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if !ck || !rk || !sk {
			return AnimalContainmentMethod{}, errors.New("animal containment state unknown")
		}
		if contained || release || slaughter {
			continue
		}
		rows = append(rows, a)
		if suitable, known := a.SuitablePen.Value(); !known || suitable == "" {
			allSuitable = false
		}
	}
	sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
	ids := make([]PawnID, len(rows))
	for i, a := range rows {
		ids[i] = a.ID
	}
	if len(rows) == 0 {
		return AnimalContainmentMethod{Reason: ContainmentNoDeficit}, nil
	}
	if allSuitable {
		available, known := handlerAvailable.Value()
		if !known {
			return AnimalContainmentMethod{}, errors.New("handler availability unknown")
		}
		if !available {
			return AnimalContainmentMethod{Reason: ContainmentWaitingHandler, Animals: ids}, nil
		}
		return AnimalContainmentMethod{Reason: ContainmentWaitingNativePen, Animals: ids}, nil
	}
	if len(rows) > maxAnimalContainmentHerd {
		return AnimalContainmentMethod{Reason: ContainmentExceedsBound, Animals: ids}, nil
	}
	switch shell {
	case ContainmentShellNone:
		return AnimalContainmentMethod{Reason: ContainmentBuildShell, Animals: ids}, nil
	case ContainmentShellPending:
		return AnimalContainmentMethod{Reason: ContainmentAwaitingShell, Animals: ids}, nil
	}
	if markerAttempted {
		return AnimalContainmentMethod{Reason: ContainmentMarkerExhausted, Animals: ids}, nil
	}
	return AnimalContainmentMethod{Reason: ContainmentPlaceMarker, Animals: ids}, nil
}
