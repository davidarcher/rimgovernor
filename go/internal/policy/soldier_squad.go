package policy

import (
	"slices"
	"sort"
)

// SoldierSquad is the bot's persistent soldier squad: the colony's
// best fighters, about a third of the colonists. Membership is sticky; the
// store keeps it in the save. GearSoldier (drafted only) is separate.
type SoldierSquad struct {
	Members []PawnID // sorted
}

// IsSoldier reports whether pawn is a squad member.
func (s SoldierSquad) IsSoldier(pawn PawnID) bool {
	_, ok := slices.BinarySearch(s.Members, pawn)
	return ok
}

// SoldierSquadSize is ceil(colonists/3), at least 1.
func SoldierSquadSize(colonists int) int {
	return max(1, (colonists+2)/3)
}

// soldierScore ranks a fighter: the better of Shooting and Melee, the passion
// of that skill breaking a tie. ok is false when the pawn cannot be a soldier
// (violence-incapable, a child) or a deciding fact is unknown; known is false
// only for the unknown case.
func soldierScore(p WorkPawn) (score int, ok, known bool) {
	age, ak := p.Age.Value()
	incapable, ik := p.Incapable.Value()
	skills, sk := p.Skills.Value()
	if !ak || !ik || !sk {
		return 0, false, false
	}
	if age < ChildAge {
		return 0, false, true
	}
	for _, w := range incapable {
		if w == "Violent" {
			return 0, false, true
		}
	}
	score = -1
	for _, s := range skills {
		if s.Disabled || s.Name != "Shooting" && s.Name != "Melee" {
			continue
		}
		score = max(score, s.Level*3+gearPassionRank(s.Passion))
	}
	return score, score >= 0, true
}

// ReviewSoldierSquad returns the squad for the current colonists. Members
// still present and fit stay; the gap to SoldierSquadSize(len(pawns)) is
// filled with the best non-members, and a shrunken target drops the weakest
// members. known is false when a pawn's deciding facts are unknown; the
// caller then keeps the previous squad.
func ReviewSoldierSquad(previous SoldierSquad, pawns []WorkPawn) (SoldierSquad, bool) {
	type fighter struct {
		id     PawnID
		score  int
		member bool
	}
	var members, others []fighter
	for _, p := range pawns {
		score, ok, known := soldierScore(p)
		if !known {
			return previous, false
		}
		if !ok {
			continue
		}
		f := fighter{p.ID, score, previous.IsSoldier(p.ID)}
		if f.member {
			members = append(members, f)
		} else {
			others = append(others, f)
		}
	}
	better := func(list []fighter) {
		sort.Slice(list, func(i, j int) bool {
			if list[i].score != list[j].score {
				return list[i].score > list[j].score
			}
			return list[i].id < list[j].id
		})
	}
	better(members)
	better(others)
	size := SoldierSquadSize(len(pawns))
	if len(members) > size {
		members = members[:size]
	}
	for _, f := range others {
		if len(members) >= size {
			break
		}
		members = append(members, f)
	}
	out := SoldierSquad{Members: make([]PawnID, 0, len(members))}
	for _, f := range members {
		out.Members = append(out.Members, f.id)
	}
	slices.Sort(out.Members)
	return out, true
}
