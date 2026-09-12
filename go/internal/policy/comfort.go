package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type ComfortFacility struct {
	ID                  string
	AccessibleTo, Users []PawnID
}
type DiningSurface struct {
	ID       string
	Adjacent []domain.Cell
}
type ComfortObservation struct {
	People             []PawnID
	Surfaces           []DiningSurface
	Dining, Recreation []ComfortFacility
}
type ComfortUse struct {
	Facility string
	Tick     domain.Tick
}
type ComfortHistory struct{ Dining, Recreation ComfortUse }
type ComfortNeed string

const (
	ComfortUnknown   ComfortNeed = "unknown"
	ComfortCapacity  ComfortNeed = "capacity"
	ComfortUseNeeded ComfortNeed = "use"
	ComfortRecovered ComfortNeed = "recovered"
)

type ComfortReview struct {
	History                                  ComfortHistory
	Dining, Recreation                       ComfortNeed
	MissingDining, MissingRecreation, People int
}

func (h ComfortHistory) Validate() error {
	for _, proof := range []ComfortUse{h.Dining, h.Recreation} {
		if proof.Tick < 0 || proof.Facility == "" && proof.Tick != 0 || proof.Facility != "" && !foodID(proof.Facility) {
			return errors.New("invalid comfort use history")
		}
	}
	return nil
}

func (v ComfortObservation) Validate() error {
	if len(v.People) > 256 || len(v.Surfaces) > 256 || len(v.Dining) > 256 || len(v.Recreation) > 256 {
		return errors.New("comfort census exceeds bound")
	}
	people := map[PawnID]bool{}
	for _, id := range v.People {
		if !foodID(string(id)) || people[id] {
			return errors.New("invalid comfort pawn")
		}
		people[id] = true
	}
	surfaces := map[string]bool{}
	for _, s := range v.Surfaces {
		if !foodID(s.ID) || surfaces[s.ID] || len(s.Adjacent) > 4096 {
			return errors.New("invalid dining surface")
		}
		surfaces[s.ID] = true
		seen := map[domain.Cell]bool{}
		for _, c := range s.Adjacent {
			if c.X < 0 || c.Z < 0 || c.X >= 4096 || c.Z >= 4096 || seen[c] {
				return errors.New("invalid dining adjacency")
			}
			seen[c] = true
		}
	}
	for _, facilities := range [][]ComfortFacility{v.Dining, v.Recreation} {
		seen := map[string]bool{}
		for _, f := range facilities {
			if !foodID(f.ID) || seen[f.ID] {
				return errors.New("invalid comfort facility")
			}
			seen[f.ID] = true
			for _, ids := range [][]PawnID{f.AccessibleTo, f.Users} {
				if len(ids) > 256 {
					return errors.New("comfort facility census exceeds bound")
				}
				found := map[PawnID]bool{}
				for _, id := range ids {
					if !people[id] || found[id] {
						return errors.New("invalid comfort facility pawn")
					}
					found[id] = true
				}
			}
		}
	}
	return nil
}

func ReviewComfort(observed domain.Fact[ComfortObservation], previous ComfortHistory, tick domain.Tick) (ComfortReview, error) {
	r := ComfortReview{History: previous, Dining: ComfortUnknown, Recreation: ComfortUnknown}
	if err := previous.Validate(); err != nil {
		return r, err
	}
	if tick < 0 || previous.Dining.Tick > tick || previous.Recreation.Tick > tick {
		return r, errors.New("comfort use is from a future tick")
	}
	v, known := observed.Value()
	if !known {
		return r, nil
	}
	if err := v.Validate(); err != nil {
		return r, err
	}
	r.People = len(v.People)
	assess := func(facilities []ComfortFacility, proof *ComfortUse) (ComfortNeed, int) {
		available := map[PawnID]bool{}
		seenUse := false
		// A stable tie-break makes simultaneous observed uses replayable.
		ordered := append([]ComfortFacility(nil), facilities...)
		sort.Slice(ordered, func(i, j int) bool { return ordered[i].ID < ordered[j].ID })
		for _, f := range ordered {
			access := map[PawnID]bool{}
			for _, id := range f.AccessibleTo {
				access[id] = true
				available[id] = true
			}
			for _, id := range f.Users {
				if access[id] {
					*proof = ComfortUse{Facility: f.ID, Tick: tick}
					break
				}
			}
		}
		for _, f := range facilities {
			seenUse = seenUse || f.ID == proof.Facility && len(f.AccessibleTo) > 0
		}
		missing := len(v.People) - len(available)
		if missing > 0 {
			return ComfortCapacity, missing
		}
		if len(v.People) > 0 && !seenUse {
			return ComfortUseNeeded, 0
		}
		return ComfortRecovered, 0
	}
	r.Dining, r.MissingDining = assess(v.Dining, &r.History.Dining)
	r.Recreation, r.MissingRecreation = assess(v.Recreation, &r.History.Recreation)
	return r, nil
}

func (r ComfortReview) Recovered() domain.Fact[bool] {
	if r.Dining == ComfortUnknown || r.Recreation == ComfortUnknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(r.Dining == ComfortRecovered && r.Recreation == ComfortRecovered)
}

func (r ComfortReview) Deficit() domain.Fact[float64] {
	if r.Dining == ComfortUnknown || r.Recreation == ComfortUnknown {
		return domain.Unknown[float64]()
	}
	// Python development ranking counts each unmet facility kind equally,
	// whether it needs capacity or proof of use.
	value := 0.0
	if r.Dining != ComfortRecovered {
		value += .5
	}
	if r.Recreation != ComfortRecovered {
		value += .5
	}
	return domain.Known(value)
}

type ComfortMethod string

const (
	ComfortNoMethod        ComfortMethod = "recovered"
	ComfortWait            ComfortMethod = "wait_for_native_use"
	ComfortAccessBlocked   ComfortMethod = "preserve_existing_facility_access"
	ComfortBuildTable      ComfortMethod = "Table1x2c"
	ComfortBuildChair      ComfortMethod = "DiningChair"
	ComfortBuildRecreation ComfortMethod = "HorseshoesPin"
)

func SelectComfortMethod(v ComfortObservation, r ComfortReview) (ComfortMethod, error) {
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
	if r.Dining == ComfortUseNeeded || r.Recreation == ComfortUseNeeded {
		return ComfortWait, nil
	}
	return ComfortNoMethod, nil
}
