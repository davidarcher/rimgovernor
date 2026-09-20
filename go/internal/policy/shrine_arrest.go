package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ShrineArrestTarget routes the same OccupantCapture decision the journal
// records. Ordinary visitors, hostile ancients and downed capture candidates
// cannot enter this standing-neutral path. Unknown capacity authorizes nobody.
func ShrineArrestTarget(f RoutineFacts) domain.PawnID {
	room, known := JoinerCapacity(f.JoinerCapacity()).Value()
	if !known || !room {
		return ""
	}
	shrines, known := f.Upkeep.Shrines.Value()
	if !known {
		return ""
	}
	var target domain.PawnID
	for _, shrine := range shrines {
		for _, occupant := range shrine.Occupants {
			if occupant.Faction != "Ancients" || occupant.Downed || occupant.Hostile || OccupantDecision(occupant, room) != OccupantCapture {
				continue
			}
			pawn := domain.PawnID(occupant.EntityID)
			if target == "" || pawn < target {
				target = pawn
			}
		}
	}
	return target
}

// ShrineArrestBed selects an unoccupied, unowned humanlike prisoner bed.
// Native Arrest rechecks suitability, reach and reservations before ordering.
func ShrineArrestBed(sleeping domain.Fact[SleepingObservation]) string {
	census, known := sleeping.Value()
	if !known {
		return ""
	}
	bed := ""
	for _, candidate := range census.Beds {
		human, hk := candidate.Humanlike.Value()
		prisoner, pk := candidate.Prisoners.Value()
		if !hk || !pk || !human || !prisoner || len(candidate.Users) != 0 || len(candidate.Owners) != 0 {
			continue
		}
		if bed == "" || candidate.ID < bed {
			bed = candidate.ID
		}
	}
	return bed
}

func ShrineArrester(squad []ShrineDefenderFacts) domain.PawnID {
	var pawn domain.PawnID
	for _, candidate := range squad {
		if shrineDefenderEligible(candidate) && (pawn == "" || candidate.ID < pawn) {
			pawn = candidate.ID
		}
	}
	return pawn
}
