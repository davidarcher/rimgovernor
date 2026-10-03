package policy

import "sort"

// Room quality ranking (#813, B2 of #799): a Jealous colonist holds the
// colony's best bedroom and an Ascetic colonist the plainest. Both are met
// by swapping existing bedrooms one AssignIntent at a time: the mover takes the
// other room's bed, the native claim evicts its owner, and the sleeping
// planner then assigns the evicted owner a vacant bed (the mover's old one
// among them; plainest first for an ascetic, see SelectSleepingMethod).
//
// Only solo bedrooms swap: a room whose one owned humanlike bed has exactly
// one owner, so no move ever pairs non-partners or splits a couple.

// soloBedroom is one solo bedroom with a known impressiveness.
type soloBedroom struct {
	room, bed      string
	owner          PawnID
	impressiveness float64
}

func soloBedrooms(obs SleepingObservation) []soloBedroom {
	rooms, ok := obs.Rooms.Value()
	if !ok {
		return nil
	}
	impressiveness := map[string]float64{}
	for _, room := range rooms {
		if q, ok := room.Quality.Value(); ok {
			impressiveness[room.ID] = q.Impressiveness
		}
	}
	owned := map[string][]SleepingBed{}
	for _, bed := range obs.Beds {
		room, ok := bed.Room.Value()
		humanlike, _ := bed.Humanlike.Value()
		medical, _ := bed.Medical.Value()
		prisoners, _ := bed.Prisoners.Value()
		if !ok || room == "" || !humanlike || medical || prisoners || bed.Slaves || len(bed.Owners) == 0 {
			continue
		}
		owned[room] = append(owned[room], bed)
	}
	var out []soloBedroom
	for room, beds := range owned {
		v, ok := impressiveness[room]
		if !ok || len(beds) != 1 || len(beds[0].Owners) != 1 {
			continue
		}
		out = append(out, soloBedroom{room, beds[0].ID, beds[0].Owners[0], v})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].room < out[j].room })
	return out
}

// BedroomSwap is one AssignIntent moving Pawn out of PreviousBed into Bed.
type BedroomSwap struct {
	Pawn             PawnID
	Bed, PreviousBed string
}

// NextBedroomSwap returns the next swap the ranking wants, false when none.
// Jealous colonists go first: one in a solo bedroom less impressive than the
// best solo bedroom takes it from a non-jealous owner. Then an ascetic in a
// solo bedroom more impressive than the plainest takes that one from an
// owner neither ascetic nor jealous. Each swap strictly moves the better room
// to the colonist who wants it, so the ranking settles; two jealous
// colonists are never displaced for each other.
//
// Suites (the census ids in suites, #1216) never swap: each is its
// claimant's, so a Jealous colonist outdone by a suite earns a suite of
// their own (SuiteClaims) and an ascetic is never moved into one.
func NextBedroomSwap(obs SleepingObservation, traits map[PawnID]TraitEffects, suites map[string]bool) (BedroomSwap, bool) {
	var rooms []soloBedroom
	for _, r := range soloBedrooms(obs) {
		if !suites[r.room] {
			rooms = append(rooms, r)
		}
	}
	accessible := map[string][]PawnID{}
	for _, b := range obs.Beds {
		accessible[b.ID] = b.AccessibleTo
	}
	pick := func(mover soloBedroom, better func(a, b float64) bool, blocked func(TraitEffects) bool) (BedroomSwap, bool) {
		var best *soloBedroom
		for i := range rooms {
			r := &rooms[i]
			if r.owner == mover.owner || blocked(traits[r.owner]) || !containsPawn(accessible[r.bed], mover.owner) {
				continue
			}
			if better(r.impressiveness, mover.impressiveness) && (best == nil || better(r.impressiveness, best.impressiveness)) {
				best = r
			}
		}
		if best == nil {
			return BedroomSwap{}, false
		}
		return BedroomSwap{Pawn: mover.owner, Bed: best.bed, PreviousBed: mover.bed}, true
	}
	higher := func(a, b float64) bool { return a > b }
	lower := func(a, b float64) bool { return a < b }
	for _, r := range rooms {
		if traits[r.owner].Jealous {
			if s, ok := pick(r, higher, func(e TraitEffects) bool { return e.Jealous }); ok {
				return s, true
			}
		}
	}
	for _, r := range rooms {
		if e := traits[r.owner]; e.Ascetic && !e.Jealous {
			if s, ok := pick(r, lower, func(e TraitEffects) bool { return e.Ascetic || e.Jealous }); ok {
				return s, true
			}
		}
	}
	return BedroomSwap{}, false
}
