package policy

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BasicComfortReview is the foothold half of the comfort need: every
// colonist can reach a seat at an indoor eating surface and a recreation
// source, wherever they stand. "Ate without table" and "no recreation" are
// the cheapest mood debuffs to remove, so they are provided with the
// starter hut rather than after the whole startup ladder; the hosting
// room's native role and proof of use stay EnsureComfort's ranked concern.
type BasicComfortReview struct {
	Dining, Recreation                       ComfortNeed
	MissingDining, MissingRecreation, People int
}

// ReviewBasicComfort measures the unfiltered comfort census (every indoor
// seat and every recreation source, whatever room hosts it). Capacity alone
// counts: a facility everyone can reach is provided whether or not anyone
// has been observed using it yet.
func ReviewBasicComfort(observed domain.Fact[ComfortObservation]) (BasicComfortReview, error) {
	r := BasicComfortReview{Dining: ComfortUnknown, Recreation: ComfortUnknown}
	full, err := ReviewComfort(observed, ComfortHistory{}, 0)
	if err != nil {
		return r, err
	}
	provided := func(need ComfortNeed) ComfortNeed {
		if need == ComfortUseNeeded {
			return ComfortRecovered
		}
		return need
	}
	r.Dining, r.Recreation = provided(full.Dining), provided(full.Recreation)
	r.MissingDining, r.MissingRecreation, r.People = full.MissingDining, full.MissingRecreation, full.People
	return r, nil
}

func (r BasicComfortReview) Recovered() domain.Fact[bool] {
	if r.Dining == ComfortUnknown || r.Recreation == ComfortUnknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(r.Dining == ComfortRecovered && r.Recreation == ComfortRecovered)
}

func (r BasicComfortReview) Deficit() domain.Fact[float64] {
	if r.Dining == ComfortUnknown || r.Recreation == ComfortUnknown {
		return domain.Unknown[float64]()
	}
	value := 0.0
	if r.Dining != ComfortRecovered {
		value += .5
	}
	if r.Recreation != ComfortRecovered {
		value += .5
	}
	return domain.Known(value)
}

// SelectBasicComfortMethod names the one facility the foothold goal builds
// next: a table where no indoor eating surface stands, then a seat beside
// it, then a recreation source. Existing furniture some colonist cannot
// reach is preserved as a blocker, as EnsureComfort does, rather than
// duplicated.
func SelectBasicComfortMethod(v ComfortObservation, r BasicComfortReview) (ComfortMethod, error) {
	if err := v.Validate(); err != nil {
		return "", err
	}
	if r.Dining == ComfortUnknown || r.Recreation == ComfortUnknown {
		return "", errors.New("comfort evidence unavailable")
	}
	if r.Dining == ComfortCapacity {
		if len(v.Dining) > 0 {
			return ComfortAccessBlocked, nil
		}
		if len(v.Surfaces) == 0 {
			return ComfortBuildTable, nil
		}
		return ComfortBuildChair, nil
	}
	if r.Recreation == ComfortCapacity {
		if len(v.Recreation) > 0 {
			return ComfortAccessBlocked, nil
		}
		return ComfortBuildRecreation, nil
	}
	return ComfortNoMethod, nil
}
