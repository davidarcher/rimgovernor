package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const MaintainSleeping GoalID = "MaintainSleeping"

type SleepingPerson struct {
	ID                             PawnID
	OwnedBed                       domain.Fact[string]
	ComfortableMin, ComfortableMax domain.Fact[float64]
}
type SleepingBed struct {
	ID                                    string
	Definition                            Resource
	Humanlike, Medical, Prisoners, Roofed domain.Fact[bool]
	RestEffectiveness, Temperature        domain.Fact[float64]
	Owners, Users, AccessibleTo           []PawnID
}
type SleepingObservation struct {
	Colonists int
	People    []SleepingPerson
	Beds      []SleepingBed
}
type SleepingUse struct {
	Pawn PawnID
	Bed  string
	Tick domain.Tick
}
type SleepingHistory struct{ Uses []SleepingUse }
type SleepingNeedKind string

const (
	SleepingUpgrade   SleepingNeedKind = "upgrade"
	SleepingUnsafe    SleepingNeedKind = "unsafe"
	SleepingUseNeeded SleepingNeedKind = "use"
)

type SleepingTarget struct {
	Pawn        PawnID
	Kind        SleepingNeedKind
	PreviousBed string
	Available   []string
}
type SleepingReview struct {
	History SleepingHistory
	Targets domain.Fact[[]SleepingTarget]
}

func (h SleepingHistory) Validate() error {
	if len(h.Uses) > 256 {
		return errors.New("sleeping history exceeds bound")
	}
	seen := map[PawnID]bool{}
	for _, use := range h.Uses {
		if !foodID(string(use.Pawn)) || !foodID(use.Bed) || use.Tick < 0 || seen[use.Pawn] {
			return errors.New("invalid sleeping use history")
		}
		seen[use.Pawn] = true
	}
	return nil
}

// Use is retained only for the same pawn and exact bed identity. The journal
// resets history when the colony/load/map changes or time moves backwards.
func ReviewSleeping(observed domain.Fact[SleepingObservation], previous SleepingHistory, tick domain.Tick) (SleepingReview, error) {
	r := SleepingReview{History: SleepingHistory{Uses: append([]SleepingUse{}, previous.Uses...)}}
	invalid := errors.New("invalid sleeping census")
	if err := previous.Validate(); err != nil {
		return r, err
	}
	if tick < 0 {
		return r, invalid
	}
	uses := map[PawnID]SleepingUse{}
	for _, use := range previous.Uses {
		if use.Tick > tick {
			return r, invalid
		}
		uses[use.Pawn] = use
	}
	v, known := observed.Value()
	if !known {
		return r, nil
	}
	if v.Colonists < 0 || v.Colonists > 256 || len(v.People) > 256 || len(v.Beds) > 256 {
		return r, invalid
	}
	if len(v.People) != v.Colonists {
		return r, nil
	}
	finite := func(f domain.Fact[float64]) bool { n, k := f.Value(); return k && !math.IsNaN(n) && !math.IsInf(n, 0) }
	people := map[PawnID]bool{}
	complete := true
	for _, p := range v.People {
		if !foodID(string(p.ID)) || people[p.ID] {
			return r, invalid
		}
		people[p.ID] = true
		bed, bk := p.OwnedBed.Value()
		if bk && bed != "" && !foodID(bed) {
			return r, invalid
		}
		if !bk || !finite(p.ComfortableMin) || !finite(p.ComfortableMax) {
			complete = false
			continue
		}
		lo, _ := p.ComfortableMin.Value()
		hi, _ := p.ComfortableMax.Value()
		if lo > hi {
			return r, invalid
		}
	}
	beds := map[string]SleepingBed{}
	for _, b := range v.Beds {
		if !foodID(b.ID) || !validResource(b.Definition) {
			return r, invalid
		}
		if _, exists := beds[b.ID]; exists {
			return r, invalid
		}
		beds[b.ID] = b
		if !finite(b.RestEffectiveness) || !finite(b.Temperature) {
			complete = false
		}
		for _, f := range []domain.Fact[bool]{b.Humanlike, b.Medical, b.Prisoners, b.Roofed} {
			if _, k := f.Value(); !k {
				complete = false
			}
		}
		for _, ids := range [][]PawnID{b.Owners, b.Users, b.AccessibleTo} {
			if len(ids) > 256 {
				return r, invalid
			}
			seen := map[PawnID]bool{}
			for _, id := range ids {
				if !foodID(string(id)) || seen[id] {
					return r, invalid
				}
				seen[id] = true
			}
		}
	}
	if !complete {
		return r, nil
	}
	contains := func(ids []PawnID, id PawnID) bool {
		for _, p := range ids {
			if p == id {
				return true
			}
		}
		return false
	}
	targets := []SleepingTarget{}
	next := []SleepingUse{}
	ordered := append([]SleepingPerson{}, v.People...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].ID < ordered[j].ID })
	for _, p := range ordered {
		lo, _ := p.ComfortableMin.Value()
		hi, _ := p.ComfortableMax.Value()
		safe := func(b SleepingBed) bool {
			human, _ := b.Humanlike.Value()
			medical, _ := b.Medical.Value()
			prisoner, _ := b.Prisoners.Value()
			roof, _ := b.Roofed.Value()
			temperature, _ := b.Temperature.Value()
			return human && !medical && !prisoner && roof && contains(b.AccessibleTo, p.ID) && temperature >= lo && temperature <= hi
		}
		suitable := func(b SleepingBed) bool {
			rest, _ := b.RestEffectiveness.Value()
			return safe(b) && b.Definition != "SleepingSpot" && rest > 0
		}
		bedID, _ := p.OwnedBed.Value()
		owned, exists := beds[bedID]
		kind := SleepingUpgrade
		if exists && suitable(owned) && contains(owned.Owners, p.ID) {
			if contains(owned.Users, p.ID) {
				uses[p.ID] = SleepingUse{p.ID, owned.ID, tick}
			}
			if use, ok := uses[p.ID]; ok && use.Bed == owned.ID {
				next = append(next, use)
				continue
			}
			kind = SleepingUseNeeded
		} else if exists && !safe(owned) {
			kind = SleepingUnsafe
		}
		// Keep previous exact-bed proof while temporarily unsafe; it cannot clear a
		// need unless that same assignment is observed suitable again.
		if use, ok := uses[p.ID]; ok {
			next = append(next, use)
		}
		available := []string{}
		for _, b := range v.Beds {
			if suitable(b) && len(b.Owners) == 0 {
				available = append(available, b.ID)
			}
		}
		sort.Strings(available)
		targets = append(targets, SleepingTarget{p.ID, kind, bedID, available})
	}
	r.History.Uses = next
	r.Targets = domain.Known(targets)
	return r, nil
}

func (r SleepingReview) Recovered() domain.Fact[bool] {
	rows, known := r.Targets.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(rows) == 0)
}
