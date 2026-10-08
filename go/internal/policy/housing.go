package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Phase is the step a phased goal (MaintainHousing, EnsureComfort,
// MaintainMedicalReserves) leaves owed, latched per goal in RoundsLatches.
// MaintainHousing walks its phases in order and each has its own planner: the starter shelter
// with its bunks, then bed ownership and bedrooms, then the spare room.
type Phase string

const (
	// HousingShelter: the foothold gates (a sleeping place and an indoor
	// place for everyone) are not both met.
	HousingShelter Phase = "shelter"
	// HousingSleeping: ReviewSleeping has a colonist without a suitable
	// owned bed, an unused one or a bedroom step due.
	HousingSleeping Phase = "sleeping"
	// HousingExpansion: indoor capacity keeps no spare place beyond the
	// population. Raised from StageReserves.
	HousingExpansion Phase = "expansion"
)

// HousingReview is MaintainHousing's goal and assessment for one review.
type HousingReview struct {
	Phase     Phase
	Priority  int
	Deficit   domain.Fact[float64]
	Recovered domain.Fact[bool]
}

// reviewHousing is MaintainHousing, the one housing goal: the starter
// shelter at foothold priority first, then sleeping upkeep, then expansion.
// A later phase never opens while an earlier one is owed, so the first
// shelter and its beds come before any bedroom or spare room. A sleeping
// census that is unknown and not latched active does not hold expansion.
func reviewHousing(f RoundsFacts, previous RoundsLatches, p RoundsPolicy, sleepingActive bool) HousingReview {
	shelter := allFacts(footholdShelter(f), footholdSleeping(f))
	// The upkeep census counts any issued housing plan; only one the
	// sleeping phase issued holds that phase open.
	issued := f.UpkeepIssued[MaintainHousing] && previous.Housing == HousingSleeping
	sleeping := f.SleepingRecovered
	if issued {
		sleeping = domain.Known(false)
	}
	_, sleepingKnown := sleeping.Value()
	sleepingPriority := 3
	if !sleepingKnown && !sleepingActive {
		sleepingPriority = 4
	}
	expansion := domain.Unknown[bool]()
	if n, known := f.Colonists.Value(); known && n > 0 {
		expansion = measured(f.IndoorCapacity, func(capacity int64) bool { return capacity > n })
	}
	staged := p.ColonyStage >= StageReserves
	recovered := allFacts(shelter, sleeping)
	if staged {
		recovered = allFacts(recovered, expansion)
	}
	switch {
	case !positive(shelter):
		return HousingReview{Phase: HousingShelter, Priority: 2, Deficit: domain.Unknown[float64](), Recovered: shelter}
	case !positive(sleeping) && (sleepingKnown || sleepingActive):
		return sleepingHousing(sleeping, sleepingPriority)
	case staged && !positive(expansion):
		return HousingReview{Phase: HousingExpansion, Priority: 4, Deficit: RoundsDeficit(MaintainHousing, f, p), Recovered: expansion}
	case !positive(sleeping):
		return sleepingHousing(sleeping, sleepingPriority)
	}
	return HousingReview{Priority: 2, Recovered: recovered}
}

// sleepingHousing is the bedroom phase: a confirmed deficit ranks at
// Known(1.0); an unknown census stays DevelopmentUnknown.
func sleepingHousing(sleeping domain.Fact[bool], priority int) HousingReview {
	deficit := domain.Unknown[float64]()
	if _, known := sleeping.Value(); known {
		deficit = domain.Known(1.0)
	}
	return HousingReview{Phase: HousingSleeping, Priority: priority, Deficit: deficit, Recovered: sleeping}
}

// Phase is goal's latched phase; empty for an unphased goal or once the goal
// recovered.
func (l RoundsLatches) Phase(goal ConcernID) Phase {
	switch goal {
	case MaintainHousing:
		return l.Housing
	case EnsureComfort:
		return l.Comfort
	case MaintainMedicalReserves:
		return l.Medical
	}
	return ""
}
