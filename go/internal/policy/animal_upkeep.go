package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	MaintainAnimalContainment GoalID = "MaintainAnimalContainment"
	MaintainAnimalFeed        GoalID = "MaintainAnimalFeed"
)

type UpkeepAnimal struct {
	// Pest is the race row's flag (AnimalRace.Pest, #1722): a wild animal
	// ClearPests hunts for what it destroys.
	Pest          bool `json:",omitempty"`
	SupportsAreas domain.Fact[bool]
	AllowedArea   domain.Fact[string]
	ID            PawnID
	Definition    Resource
	// Label is the native display name; empty when the census omits it.
	Label string
	// Gender is native Gender ("Male", "Female", "None"); empty when unread.
	Gender                                     string
	RequiresPen, Contained, Release, Slaughter domain.Fact[bool]
	Pen, SuitablePen                           domain.Fact[string]
	// SafeToSlaughter and Training carry MaintainHerd-*'s husbandry
	// eligibility straight from the same generic census read that already
	// decodes containment/feed facts (AnimalState already exposes both), so
	// no dedicated per-cycle husbandry read is needed to detect the deficit.
	SafeToSlaughter domain.Fact[bool]
	SafeToRelease   domain.Fact[bool]
	Training        []HusbandryTrainable
	// Tameable and Tame are only populated for the wild census
	// (AnimalUpkeepObservation.WildAnimals): native tame eligibility and a
	// standing tame designation.
	Tameable, Tame domain.Fact[bool]
	// MinimumHandlingSkill is the Animals level taming the wild animal
	// needs (native's TrainableUtility.MinimumHandlingSkill); TamerFor
	// answers whether the roster has it.
	MinimumHandlingSkill domain.Fact[int]
	// Herd carries the sizing facts MaintainHerd culls and tames by (#875).
	Herd HerdFacts
	// ReachableBenches names the player work tables the animal can reach
	// inside its allowed area (sorted); a bill there drops feed where the
	// animal eats it.
	ReachableBenches []string
	// ReachableStorage lists the stockpile zones holding a cell the animal
	// can reach inside its allowed area, each with the edible definitions
	// its filter accepts: feed hauled there is feed the animal eats.
	// StorageCandidates is a connected footprint of free cells inside the
	// area (native's own hauler-reachable scan) where such a zone could be
	// made; empty when none qualifies.
	ReachableStorage  []AnimalFeedStorage
	StorageCandidates []domain.Cell
	// Care cap inputs (#1301): MedicalCareCategory name, a bond to a
	// living pawn, conditions and life threat.
	Care   domain.Fact[string]
	Bonded domain.Fact[bool]
	// BondedPawns are the living humanlike pawns the animal has a Bond
	// relation with (any status); a companion's master is one on the roster.
	BondedPawns []string
	// Master is the colonist id mastering the animal ("" unassigned);
	// Obedient is learned Obedience, which native requires to master or
	// follow (#1635).
	Master domain.Fact[string]
	// Sterilized is the Sterilized hediff; SterilizeQueued a sterilize
	// surgery bill waiting on the animal.
	Sterilized, SterilizeQueued              domain.Fact[bool]
	FollowDrafted, FollowFieldwork, Obedient domain.Fact[bool]
	Conditions                               domain.Fact[[]CareCondition]
	LifeThreatening                          domain.Fact[bool]
}

// AnimalFeedStorage is one stockpile zone an animal can reach and the
// nutrition-giving definitions its filter accepts among those the animal's
// race can eat.
type AnimalFeedStorage struct {
	Zone    string
	Accepts []string
}

// HusbandryTrainable is one trainable definition's recursive-training
// eligibility for one animal, ported from AnimalState.TrainingEntry.
type HusbandryTrainable struct {
	Def                string
	Available, Learned domain.Fact[bool]
}
type AnimalUpkeepObservation struct {
	Forecast domain.Fact[FoodForecast]
	Animals  domain.Fact[[]UpkeepAnimal]
	// WildAnimals is the factionless census MaintainHerd tames from; it
	// carries no feed or pen facts.
	WildAnimals domain.Fact[[]UpkeepAnimal]
	// AnimalRaces is the load's race catalog (#1625), read with every
	// routine reading.
	AnimalRaces   AnimalRaceCatalog
	Food          domain.Fact[FoodSupply]
	DirectedHerds []Resource
}

// AnimalUpkeepHistory is the containment latch MaintainAnimalContainment
// stays active on until its review next finds no uncontained animal.
type AnimalUpkeepHistory struct {
	Containment bool
}

type AnimalUpkeepReview struct {
	History     AnimalUpkeepHistory
	Containment domain.Fact[[]PawnID]
	// Feed is the race groups whose herd feed reserve is short (#1642); empty
	// when every reserve is met, unknown while the herd or its food is.
	Feed domain.Fact[[]AnimalFeedGroup]
}

// ReviewAnimalUpkeep reviews containment and, per race group, the standing
// herd feed reserve of reserveDays days (RoutinePolicy.FoodReserveDays).
func ReviewAnimalUpkeep(v AnimalUpkeepObservation, previous AnimalUpkeepHistory, reserveDays float64) (AnimalUpkeepReview, error) {
	r := AnimalUpkeepReview{History: previous}
	invalid := errors.New("invalid animal upkeep facts or history")
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
	seen := map[PawnID]bool{}
	containment := []PawnID{}
	eligible := []UpkeepAnimal{}
	containmentKnown, feedKnown := true, true
	for _, animal := range animals {
		if !foodID(string(animal.ID)) || seen[animal.ID] || !validResource(animal.Definition) || !validAnimalFeedStorage(animal.ReachableStorage, animal.StorageCandidates) {
			return r, invalid
		}
		for _, bench := range animal.ReachableBenches {
			if !foodID(bench) {
				return r, invalid
			}
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
			eligible = append(eligible, animal)
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
	feed, err := ReviewAnimalFeedReserve(v, eligible, reserveDays)
	if err != nil {
		return r, err
	}
	r.Feed = feed
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
	// ContainmentMarkerExhausted is the blocked outcome once a marker
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

// RaceProduct is one periodic yield of a race (#1625): Kind is milk, wool,
// eggs or spawner (chemfuel and other periodic spawns), Amount the items per
// yield and IntervalDays the days between yields at full growth.
type RaceProduct struct {
	Kind         string
	Def          Resource
	Amount       domain.Fact[float64]
	IntervalDays domain.Fact[float64]
}

// AnimalRace is the static facts of one animal race (#1625), the same for
// every animal of it and fixed for a map load. An unread value is unknown,
// never zero.
type AnimalRace struct {
	Def                  Resource
	CarryingCapacity     domain.Fact[float64]
	Wildness             domain.Fact[float64]
	BodySize             domain.Fact[float64]
	CombatPower          domain.Fact[float64]
	MarketValue          domain.Fact[float64]
	MinimumHandlingSkill domain.Fact[int]
	// Trainability is the native TrainabilityDef name (None, Simple,
	// Intermediate, Advanced).
	Trainability domain.Fact[string]
	// Trainables are the training the race can ever learn (sorted).
	Trainables []string
	Products   []RaceProduct
	// FeedItems are the items the race can eat that a recipe produces (sorted
	// by definition name): the feed a bench can make for it.
	FeedItems []RaceFeedItem
	// LifeExpectancy is RaceProperties.lifeExpectancy in years,
	// ManhunterOnTameFail and ManhunterOnDamage the manhunter chances (#1722).
	LifeExpectancy, ManhunterOnTameFail, ManhunterOnDamage domain.Fact[float64]
	// Predator is RaceProperties.predator; Pest is a wild animal that eats
	// trees (RaceProperties.Eats(Tree)), hunted for what it destroys, not for
	// meat; Mechanoid and Insect are the game's own race flags.
	Predator, Pest, Mechanoid, Insect bool
	// Edible are the foods the race can ever eat (RaceProperties.CanEverEat),
	// sorted.
	Edible []string
}

// RaceFeedItem is one producible feed item and its nutrition per item.
type RaceFeedItem struct {
	Def       Resource
	Nutrition float64
}

// AnimalRaceCatalog is every race the game knows, by definition name.
type AnimalRaceCatalog struct {
	Races map[Resource]AnimalRace
}

// Race is def's facts, false when the catalog does not know the race.
func (c AnimalRaceCatalog) Race(def Resource) (AnimalRace, bool) {
	race, ok := c.Races[def]
	return race, ok
}
