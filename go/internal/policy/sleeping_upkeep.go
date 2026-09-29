package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type SleepingPerson struct {
	ID                             PawnID
	OwnedBed                       domain.Fact[string]
	ComfortableMin, ComfortableMax domain.Fact[float64]
	// Partners are the lover, spouse and fiance relations to living pawns
	// on the same map.
	Partners []PawnID
	// BedSharingAllowed is the native willingness (ideo precepts) to share
	// a bed with every partner; true without Ideology.
	BedSharingAllowed domain.Fact[bool]
	// Title is the most senior royal title; nil without one or without
	// Royalty.
	Title *RoyalTitle
}
type SleepingBed struct {
	ID                                    string
	Definition                            Resource
	Humanlike, Medical, Prisoners, Roofed domain.Fact[bool]
	// Slaves is Building_Bed.ForSlaves; false without Ideology.
	Slaves                         bool
	RestEffectiveness, Temperature domain.Fact[float64]
	Owners, Users, AccessibleTo    []PawnID
	// Room is the native room id (RoomQuality.ID); Quality the native
	// QualityCategory name, unknown for a bed without quality.
	Room, Quality domain.Fact[string]
	// Stuff is the native stuff defName (#842), unknown for a stuffless bed.
	Stuff domain.Fact[string]
	// Cell is the bed's position (its head cell).
	Cell domain.Cell
}
type SleepingObservation struct {
	Colonists int
	People    []SleepingPerson
	// Slaves are the colony's slaves (Ideology), outside Colonists and
	// People: each needs a bed set for slaves (Building_Bed.ForSlaves),
	// which a free colonist may never own. They are never partnered.
	Slaves []SleepingPerson
	Beds   []SleepingBed
	// Rooms is the room quality census, unknown when its section is.
	Rooms domain.Fact[[]UpkeepRoom]
	// BedBuildable is Bed's native availability (#1181): while it is known
	// false a bedroll is a suitable bed, and while it is true an upgrade
	// target. Unknown keeps a bedroll suitable. A spot is never suitable.
	BedBuildable domain.Fact[bool]
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
	// SleepingShare: the pawn owns a suitable bed, but not one shared with
	// its couple partner (the same bed, or a bed in the partner's room).
	SleepingShare SleepingNeedKind = "share"
)

// SleepingDoubleBeds are the bed definitions two partners can own together.
var SleepingDoubleBeds = map[Resource]bool{"DoubleBed": true, "RoyalBed": true, SleepingCoupleBedrollDefinition: true}

// sleepingCouples maps each pawn to its couple partner: the lowest-ID love
// partner that names it back, both willing to share a bed. Unknown
// willingness pairs nobody, so non-partners are never put together.
func sleepingCouples(people []SleepingPerson) map[PawnID]PawnID {
	byID := map[PawnID]SleepingPerson{}
	for _, p := range people {
		byID[p.ID] = p
	}
	willing := func(p SleepingPerson) bool { w, k := p.BedSharingAllowed.Value(); return k && w }
	names := func(p SleepingPerson, id PawnID) bool {
		for _, q := range p.Partners {
			if q == id {
				return true
			}
		}
		return false
	}
	couples := map[PawnID]PawnID{}
	for _, p := range people {
		partners := append([]PawnID{}, p.Partners...)
		sort.Slice(partners, func(i, j int) bool { return partners[i] < partners[j] })
		for _, id := range partners {
			q, ok := byID[id]
			if ok && id != p.ID && willing(p) && willing(q) && names(q, p.ID) {
				couples[p.ID] = id
				break
			}
		}
	}
	// Keep only reciprocal choices (a pawn with two partners picks one).
	for p, q := range couples {
		if couples[q] != p {
			delete(couples, p)
		}
	}
	return couples
}

type SleepingTarget struct {
	Pawn        PawnID
	Kind        SleepingNeedKind
	PreviousBed string
	// Available lists assignable beds in preference order.
	Available []string
	// Partner is the pawn's couple partner, empty when single.
	Partner PawnID
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
	if v.Colonists < 0 || v.Colonists > 256 || len(v.People) > 256 || len(v.Slaves) > 256 || len(v.Beds) > 256 {
		return r, invalid
	}
	if len(v.People) != v.Colonists {
		return r, nil
	}
	finite := func(f domain.Fact[float64]) bool { n, k := f.Value(); return k && !math.IsNaN(n) && !math.IsInf(n, 0) }
	people := map[PawnID]bool{}
	complete := true
	// Slaves sleep only in beds set for slaves, colonists never in one.
	everyone := append(append([]SleepingPerson{}, v.People...), v.Slaves...)
	slave := map[PawnID]bool{}
	for _, p := range v.Slaves {
		slave[p.ID] = true
	}
	for _, p := range everyone {
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
	couples := sleepingCouples(v.People)
	ownedBy := map[PawnID]string{}
	for _, p := range v.People {
		ownedBy[p.ID], _ = p.OwnedBed.Value()
	}
	room := func(bed string) (string, bool) {
		b, ok := beds[bed]
		if !ok {
			return "", false
		}
		id, known := b.Room.Value()
		return id, known && id != ""
	}
	// shared: the bed holds nobody but p and its partner, and a couple
	// sleeps together (one bed, or one room).
	shared := func(b SleepingBed, p PawnID) bool {
		q, coupled := couples[p]
		for _, o := range b.Owners {
			if o != p && (!coupled || o != q) {
				return false
			}
		}
		if !coupled || contains(b.Owners, q) {
			return true
		}
		mine, mk := room(b.ID)
		theirs, tk := room(ownedBy[q])
		return mk && tk && mine == theirs
	}
	targets := []SleepingTarget{}
	next := []SleepingUse{}
	ordered := everyone
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
			return human && !medical && !prisoner && b.Slaves == slave[p.ID] && roof && contains(b.AccessibleTo, p.ID) && temperature >= lo && temperature <= hi
		}
		suitable := func(b SleepingBed) bool {
			rest, _ := b.RestEffectiveness.Value()
			return safe(b) && sleepingRung(b.Definition, v.BedBuildable) && rest > 0
		}
		bedID, _ := p.OwnedBed.Value()
		owned, exists := beds[bedID]
		kind := SleepingUpgrade
		q, coupled := couples[p.ID]
		// A double bed only p's partner may join: p waits for the partner's
		// assignment rather than moving out of it.
		waiting := exists && coupled && suitable(owned) && contains(owned.Owners, p.ID) && len(owned.Owners) == 1 && SleepingDoubleBeds[owned.Definition] && !shared(owned, p.ID)
		if exists && suitable(owned) && contains(owned.Owners, p.ID) && (shared(owned, p.ID) || waiting) {
			if waiting {
				if use, ok := uses[p.ID]; ok {
					next = append(next, use)
				}
				targets = append(targets, SleepingTarget{Pawn: p.ID, Kind: SleepingUseNeeded, PreviousBed: bedID, Partner: q})
				continue
			}
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
		} else if exists && suitable(owned) && contains(owned.Owners, p.ID) && coupled {
			kind = SleepingShare
		}
		// Keep previous exact-bed proof while temporarily unsafe; it cannot clear a
		// need unless that same assignment is observed suitable again.
		if use, ok := uses[p.ID]; ok {
			next = append(next, use)
		}
		available := []string{}
		if coupled {
			// A double bed empty or held by the partner comes first, then a
			// vacant bed in the partner's room.
			theirs, tk := room(ownedBy[q])
			var doubles, singles []string
			for _, b := range v.Beds {
				if b.ID == bedID || !suitable(b) || contains(b.Owners, p.ID) {
					continue
				}
				partnerOnly := len(b.Owners) == 0 || len(b.Owners) == 1 && b.Owners[0] == q
				if SleepingDoubleBeds[b.Definition] && partnerOnly {
					doubles = append(doubles, b.ID)
				} else if mine, mk := room(b.ID); len(b.Owners) == 0 && tk && mk && mine == theirs {
					singles = append(singles, b.ID)
				}
			}
			sort.Strings(doubles)
			sort.Strings(singles)
			available = append(doubles, singles...)
		}
		// Without a suitable bed at all, any vacant bed beats none.
		if len(available) == 0 && kind != SleepingShare {
			for _, b := range v.Beds {
				if suitable(b) && len(b.Owners) == 0 {
					available = append(available, b.ID)
				}
			}
			sort.Strings(available)
		}
		targets = append(targets, SleepingTarget{Pawn: p.ID, Kind: kind, PreviousBed: bedID, Available: available, Partner: q})
	}
	r.History.Uses = next
	r.Targets = domain.Known(targets)
	return r, nil
}

// sleepingRung reports whether a bed of this definition is a suitable bed
// on the ladder (#1181): a bedroll only while Bed is unavailable, a spot
// never, so a spot owner stays a bedroom and bedroll target (#1182).
func sleepingRung(definition Resource, bed domain.Fact[bool]) bool {
	buildable, known := bed.Value()
	switch definition {
	case SleepingSpotDefinition:
		return false
	case SleepingBedrollDefinition, SleepingCoupleBedrollDefinition:
		return !known || !buildable
	}
	return true
}

func (r SleepingReview) Recovered() domain.Fact[bool] {
	rows, known := r.Targets.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(rows) == 0)
}
