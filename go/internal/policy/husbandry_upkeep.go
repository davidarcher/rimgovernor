package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainHerd names MaintainHerd-*'s deficit: any observed,
// non-release/slaughter-flagged animal with an available-but-untrained
// trainable; any race below its food-derived population floor with a
// tameable wild animal on the map while the herd's feed forecast reports no
// shortfall; and any race above its wealth-scaled cap (PlanHerd) with a
// removable animal (herdSurplusCandidates). Removal needs no opt-in: it is
// slaughter whenever native SafeToSlaughter allows it, release only when
// slaughter is refused and SafeToRelease allows it, and never breaks the last
// breeding pair. Native checks eligibility again when the HusbandryIntent
// applies.
const MaintainHerd GoalID = "MaintainHerd"

// HerdPolicy is the per-race population band MaintainHerd plans from.
// PopulationMin drives tame designations on wild animals; PopulationMax
// drives surplus removal. A race missing from PopulationMax has no known cap
// and is never culled for surplus. FeedShort (pasture below pen demand)
// lets juveniles be culled.
type HerdPolicy struct {
	PopulationMin, PopulationMax map[Resource]int64
	// Retired races (PlanHerd) are surplus entirely: Max is 0 and no
	// breeding pair is kept.
	Retired   map[Resource]bool
	FeedShort bool
	// Roles is the herd plan's role per race: taming ranks candidates by it
	// (herdTameLess) and training follows its job (herdTrainOrder). Absent
	// for a policy not built by PlanHerd.
	Roles map[Resource]HerdRole
}

// herdTrainOrder is the trainables a race's job wants, in order. A hauler
// learns Obedience then Haul, a war animal Obedience, attack (Release) and
// Rescue as the race allows, any other job Obedience; a retiring or job-less
// race learns nothing.
func herdTrainOrder(herd HerdPolicy, race Resource) []string {
	role := herd.Roles[race]
	switch job := role.Job; {
	case role.Retiring || job == "" || job == HerdJobNone:
		return nil
	case job == HerdJobHaul:
		return []string{"Obedience", herdHaulTraining}
	case job == HerdJobWar:
		return []string{"Obedience", herdWarTraining, "Rescue"}
	}
	return []string{"Obedience"}
}

// herdTrainQueue is the animal's trainables the job wants, in training order.
func herdTrainQueue(herd HerdPolicy, a UpkeepAnimal) []HusbandryTrainable {
	var wanted []HusbandryTrainable
	for _, def := range herdTrainOrder(herd, a.Definition) {
		for _, t := range a.Training {
			if t.Def == def {
				wanted = append(wanted, t)
			}
		}
	}
	return wanted
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
	// Handler is the colonist ordered to slaughter, for
	// HusbandryPrioritizeSlaughter only.
	Handler PawnID
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
		for _, t := range herdTrainQueue(herd, a) {
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
		candidates, unknown := herdTameCandidates(rows, wildRows, herd)
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
	if removals, unknown := herdSurplusCandidates(rows, herd.PopulationMax, herd.FeedShort, herd.Retired); unknown {
		return domain.Unknown[bool]()
	} else if len(removals) > 0 {
		deficit = true
	}
	return domain.Known(deficit)
}

// herdTameCandidates computes each tracked race's shortfall
// (shortfall = target.minimum - kept player animals - pending tame
// designations) and returns the best tameable wild animals of that race
// (herdTameLess), capped to the shortfall. Kept excludes release/slaughter-designated
// animals, since those are leaving. A wild row with an unknown tameable or
// tame-designation fact, or a player row with unknown designation facts,
// makes the result unknown.
//
// The shortfall stops at the race's max (pen and feed room, #875), and a
// dangerous race (herdDangerous) outside the allowlist is never tamed.
func herdTameCandidates(rows, wild []UpkeepAnimal, herd HerdPolicy) ([]UpkeepAnimal, bool) {
	populationMin := herd.PopulationMin
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
		if tameable && !herdDangerous(a) {
			eligible[a.Definition] = append(eligible[a.Definition], a)
		}
	}
	var candidates []UpkeepAnimal
	for race, minimum := range populationMin {
		if ceiling, ok := herd.PopulationMax[race]; ok {
			minimum = min(minimum, ceiling)
		}
		shortfall := minimum - counts[race]
		if shortfall <= 0 {
			continue
		}
		rows := append([]UpkeepAnimal{}, eligible[race]...)
		role := herd.Roles[race]
		sort.Slice(rows, func(i, j int) bool { return herdTameLess(role, rows[i], rows[j]) })
		if int64(len(rows)) > shortfall {
			rows = rows[:shortfall]
		}
		candidates = append(candidates, rows...)
	}
	sort.Slice(candidates, func(i, j int) bool {
		a, b := candidates[i], candidates[j]
		if ra, rb := herdWantRank(herd.Roles[a.Definition]), herdWantRank(herd.Roles[b.Definition]); ra != rb {
			return ra < rb
		}
		if a.Definition != b.Definition {
			return a.Definition < b.Definition
		}
		return herdTameLess(herd.Roles[a.Definition], a, b)
	})
	return candidates, false
}

// herdWantRank orders races by how much the plan wants them: a preferred
// race first, then by job (herdWorkJobs order), a race with no job last.
func herdWantRank(role HerdRole) int {
	rank := slices.Index(herdWorkJobs, role.Job)
	if rank < 0 {
		rank = len(herdWorkJobs)
	}
	if !role.Preferred {
		rank += len(herdWorkJobs) + 1
	}
	return rank
}

// herdTameLess orders two wild animals of one race: the sex the plan is
// missing first (the founder rule), then the lower tame-fail risk, then the
// lower handling skill needed, then ID.
func herdTameLess(role HerdRole, a, b UpkeepAnimal) bool {
	wanted := func(u UpkeepAnimal) bool {
		return role.WantMale && u.Gender == "Male" || role.WantFemale && u.Gender == "Female"
	}
	if wa, wb := wanted(a), wanted(b); wa != wb {
		return wa
	}
	ra, _ := a.Herd.ManhunterOnTameFail.Value()
	rb, _ := b.Herd.ManhunterOnTameFail.Value()
	if ra != rb {
		return ra < rb
	}
	sa, _ := a.MinimumHandlingSkill.Value()
	sb, _ := b.MinimumHandlingSkill.Value()
	if sa != sb {
		return sa < sb
	}
	return a.ID < b.ID
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
// the first tame candidate in herdTameLess order (the race the plan wants
// most, its missing sex, then the safest and easiest) for a race below its declared minimum that
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
		for _, t := range herdTrainQueue(herd, a) {
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
		candidates, unknown := herdTameCandidates(rows, wildRows, herd)
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
	if removals, unknown := herdSurplusCandidates(rows, herd.PopulationMax, herd.FeedShort, herd.Retired); unknown {
		return HusbandryChoice{Reason: HusbandryUnknown}
	} else if len(removals) > 0 {
		return HusbandryChoice{Animal: removals[0].animal.ID, Method: removals[0].method}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
