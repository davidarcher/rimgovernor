package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainHerd names MaintainHerd-*'s deficit: any observed,
// non-release/slaughter-flagged animal with an available-but-untrained
// trainable; any race below an operator-declared HerdPopulationMin with a
// tameable wild animal on the map while the existing herd's feed forecast
// (MaintainAnimalFeed's review) reports no shortfall; and -- only once an operator has opted in
// via AllowRelease or AllowSlaughter and declared a HerdPopulationMax -- any
// surplus animal of that race safe to release or slaughter. Disclosed
// narrowing: there is no per-race protected-id, breeding-reserve or
// feed-reservation bookkeeping; surplus eligibility relies entirely on
// native's own SafeToSlaughter/SafeToRelease facts (which already exclude
// bonded/master animals per RimWorld's own rules). Slaughter is
// irreversible, so AllowSlaughter defaults to false; release is non-lethal
// but still loses the animal, so AllowRelease defaults to false too. An
// operator who never sets any of these gets exactly the training-only
// behavior this need always had. Native eligibility is still re-validated by
// EvaluateHusbandry immediately before dispatch; this only decides which
// already-observed candidate to try.
const MaintainHerd GoalID = "MaintainHerd"

// HerdPolicy is the operator-declared slice of RoutinePolicy MaintainHerd
// plans from. PopulationMin drives tame designations on wild animals;
// PopulationMax drives surplus removal, by release when AllowRelease is set
// (preferred, non-lethal) and otherwise by slaughter when AllowSlaughter is.
type HerdPolicy struct {
	AllowSlaughter, AllowRelease bool
	PopulationMin, PopulationMax map[Resource]int64
}

// HusbandryPlanReason names why RoutineHusbandryPlanner did or did not
// propose a training write.
type HusbandryPlanReason string

const (
	HusbandryNoDeficit HusbandryPlanReason = "no_untrained_trainable_or_surplus"
	HusbandryUnknown   HusbandryPlanReason = "husbandry_census_unknown"
)

// HusbandryChoice is the one animal/method pair chosen for a write,
// mirroring husbandry_method's single-write-per-cycle rule (a recursive
// training or designation write can change other requested fields, so only
// one is ever chosen). Method is set whenever Reason is empty (a choice was
// made); TrainableDef is only ever set for HusbandryTrain.
type HusbandryChoice struct {
	Reason       HusbandryPlanReason
	Animal       PawnID
	Method       domain.HusbandryMethod
	TrainableDef string
}

// AnimalHerdDeficit reports whether any observed animal still has an
// available, not-yet-learned trainable, any tracked race is below its
// minimum with a tameable wild candidate while feedShort (the herd's feed
// forecast reporting a shortfall) is known false, or -- only when a removal method
// is allowed and populationMax declares at least one tracked race -- any
// race has an observed surplus with an eligible candidate not yet
// designated. An unknown census, or any animal whose release/slaughter/
// training facts are incomplete, leaves the whole need unknown rather than
// silently treating it as recovered.
func AnimalHerdDeficit(animals, wild domain.Fact[[]UpkeepAnimal], feedShort domain.Fact[bool], herd HerdPolicy) domain.Fact[bool] {
	rows, known := animals.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	deficit := false
	for _, a := range rows {
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if !rk || !sk {
			return domain.Unknown[bool]()
		}
		if release || slaughter {
			continue
		}
		for _, t := range a.Training {
			avail, ak := t.Available.Value()
			if !ak {
				return domain.Unknown[bool]()
			}
			if !avail {
				continue
			}
			learned, lk := t.Learned.Value()
			if !lk {
				return domain.Unknown[bool]()
			}
			if !learned {
				deficit = true
			}
		}
	}
	if len(herd.PopulationMin) > 0 {
		wildRows, known := wild.Value()
		if !known {
			return domain.Unknown[bool]()
		}
		candidates, unknown := herdTameCandidates(rows, wildRows, herd.PopulationMin)
		if unknown {
			return domain.Unknown[bool]()
		}
		if len(candidates) > 0 {
			short, fk := feedShort.Value()
			if !fk {
				return domain.Unknown[bool]()
			}
			if !short {
				deficit = true
			}
		}
	}
	if method, ok := herdSurplusMethod(herd); ok {
		candidates, unknown := herdSurplusCandidates(rows, herd.PopulationMax, method)
		if unknown {
			return domain.Unknown[bool]()
		}
		if len(candidates) > 0 {
			deficit = true
		}
	}
	return domain.Known(deficit)
}

// herdSurplusMethod is the removal method an operator opted into, release
// preferred over slaughter, or none when surplus removal is not enabled.
func herdSurplusMethod(herd HerdPolicy) (domain.HusbandryMethod, bool) {
	if len(herd.PopulationMax) == 0 {
		return "", false
	}
	if herd.AllowRelease {
		return domain.HusbandryRelease, true
	}
	if herd.AllowSlaughter {
		return domain.HusbandrySlaughter, true
	}
	return "", false
}

// herdSurplusCandidates computes the herd surplus
// (surplus = len(animals) - target.maximum - pending) per tracked race,
// narrowed to a single global opt-in with no protected-id/breeding-reserve
// carve-out: eligibility is taken entirely from native's own SafeToSlaughter
// or SafeToRelease fact for the chosen method. Only races present in
// populationMax are ever counted or dispatched; an untracked race never
// blocks or contributes to a candidate list. Any tracked-race animal with an
// unknown release, slaughter or eligibility fact makes the whole result
// unknown, since an unknown fact is never evidence of a safe surplus.
// Returned candidates are deterministically ordered (lowest animal ID
// first) and already capped to each race's own surplus count.
func herdSurplusCandidates(rows []UpkeepAnimal, populationMax map[Resource]int64, method domain.HusbandryMethod) ([]UpkeepAnimal, bool) {
	counts := map[Resource]int64{}
	pending := map[Resource]int64{}
	eligible := map[Resource][]UpkeepAnimal{}
	for _, a := range rows {
		if _, tracked := populationMax[a.Definition]; !tracked {
			continue
		}
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if !rk || !sk {
			return nil, true
		}
		if release {
			continue
		}
		counts[a.Definition]++
		if slaughter {
			pending[a.Definition]++
			continue
		}
		fact := a.SafeToSlaughter
		if method == domain.HusbandryRelease {
			fact = a.SafeToRelease
		}
		safe, sak := fact.Value()
		if !sak {
			return nil, true
		}
		if safe {
			eligible[a.Definition] = append(eligible[a.Definition], a)
		}
	}
	var candidates []UpkeepAnimal
	for race, limit := range populationMax {
		surplus := counts[race] - limit - pending[race]
		if surplus <= 0 {
			continue
		}
		rows := append([]UpkeepAnimal{}, eligible[race]...)
		sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
		if int64(len(rows)) > surplus {
			rows = rows[:surplus]
		}
		candidates = append(candidates, rows...)
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
	return candidates, false
}

// herdTameCandidates computes each tracked race's shortfall
// (shortfall = target.minimum - kept player animals - pending tame
// designations) and returns the lowest-ID tameable wild animals of that
// race, capped to the shortfall. Kept excludes release/slaughter-designated
// animals, since those are leaving. A wild row with an unknown tameable or
// tame-designation fact, or a player row with unknown designation facts,
// makes the result unknown.
func herdTameCandidates(rows, wild []UpkeepAnimal, populationMin map[Resource]int64) ([]UpkeepAnimal, bool) {
	counts := map[Resource]int64{}
	eligible := map[Resource][]UpkeepAnimal{}
	for _, a := range rows {
		if _, tracked := populationMin[a.Definition]; !tracked {
			continue
		}
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if !rk || !sk {
			return nil, true
		}
		if release || slaughter {
			continue
		}
		counts[a.Definition]++
	}
	for _, a := range wild {
		if _, tracked := populationMin[a.Definition]; !tracked {
			continue
		}
		designated, dk := a.Tame.Value()
		tameable, tk := a.Tameable.Value()
		if !dk || !tk {
			return nil, true
		}
		if designated {
			counts[a.Definition]++
			continue
		}
		if tameable {
			eligible[a.Definition] = append(eligible[a.Definition], a)
		}
	}
	var candidates []UpkeepAnimal
	for race, minimum := range populationMin {
		shortfall := minimum - counts[race]
		if shortfall <= 0 {
			continue
		}
		rows := append([]UpkeepAnimal{}, eligible[race]...)
		sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
		if int64(len(rows)) > shortfall {
			rows = rows[:shortfall]
		}
		candidates = append(candidates, rows...)
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
	return candidates, false
}

// HerdFeedShort is the feed gate SelectHusbandryMethod's tame fallback
// reads from MaintainAnimalFeed's own review: known true while any player
// animal is below its feed threshold, unknown while the feed forecast is.
func HerdFeedShort(review AnimalUpkeepReview) domain.Fact[bool] {
	targets, known := review.Feed.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(targets) > 0)
}

// SelectHusbandryMethod picks the lowest animal-ID, lowest-def-name available
// untrained trainable to dispatch next, trying training first the same way
// it always has. Only once no training candidate exists does it fall back to
// the lowest-ID tame candidate for a race below its declared minimum that
// TamerFor finds a handler for at the animal's minimum handling skill (an
// unknown minimum asks only for a capable handler; an unknown roster leaves
// the choice unknown) -- and only while the herd's feed forecast is known
// not short, since a new mouth on a herd already short of feed deepens
// MaintainAnimalFeed's deficit -- and only after that to the lowest-ID
// surplus candidate for the operator's chosen removal method -- so an
// operator who opts into tame or removal never loses the pre-existing
// training behavior.
func SelectHusbandryMethod(animals, wild domain.Fact[[]UpkeepAnimal], feedShort domain.Fact[bool], herd HerdPolicy, handlers domain.Fact[[]PawnProfile]) HusbandryChoice {
	rows, known := animals.Value()
	if !known {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	sorted := append([]UpkeepAnimal{}, rows...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].ID < sorted[j].ID })
	for _, a := range sorted {
		if release, rk := a.Release.Value(); rk && release {
			continue
		}
		if slaughter, sk := a.Slaughter.Value(); sk && slaughter {
			continue
		}
		trainables := append([]HusbandryTrainable{}, a.Training...)
		sort.Slice(trainables, func(i, j int) bool { return trainables[i].Def < trainables[j].Def })
		for _, t := range trainables {
			avail, ak := t.Available.Value()
			learned, lk := t.Learned.Value()
			if ak && avail && lk && !learned {
				return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryTrain, TrainableDef: t.Def}
			}
		}
	}
	if len(herd.PopulationMin) > 0 {
		wildRows, known := wild.Value()
		if !known {
			return HusbandryChoice{Reason: HusbandryUnknown}
		}
		candidates, unknown := herdTameCandidates(rows, wildRows, herd.PopulationMin)
		if unknown {
			return HusbandryChoice{Reason: HusbandryUnknown}
		}
		if len(candidates) > 0 {
			short, fk := feedShort.Value()
			profiles, pk := handlers.Value()
			if !fk || !pk {
				return HusbandryChoice{Reason: HusbandryUnknown}
			}
			for _, a := range candidates {
				minimum, _ := a.MinimumHandlingSkill.Value()
				if _, ok := TamerFor(profiles, minimum); !short && ok {
					return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryTame}
				}
			}
		}
	}
	if method, ok := herdSurplusMethod(herd); ok {
		candidates, unknown := herdSurplusCandidates(rows, herd.PopulationMax, method)
		if unknown {
			return HusbandryChoice{Reason: HusbandryUnknown}
		}
		if len(candidates) > 0 {
			return HusbandryChoice{Animal: candidates[0].ID, Method: method}
		}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
