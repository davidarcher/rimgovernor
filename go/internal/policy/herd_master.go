package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HerdMasterChoice assigns one animal a master by the bond-first rule:
//  1. A bonded animal is mastered by its bonded colonist: the first by id
//     among its bond partners on the roster (a roster partner already
//     mastering is kept); bonds give +5 mood as master, -3 otherwise.
//  2. An unbonded animal with a wanted, available, unlearned trainable
//     (herdTrainQueue) gets the handling-capable colonist with the best
//     Animals skill; one colonist may master any number of animals. Follow
//     flags stay as they are.
//  3. An unbonded war animal gets a front-line handler and follow_drafted true.
//  4. An unbonded haul animal gets no master and no follow flag.
//
// follow_fieldwork is never written. An animal is skipped while it is not
// obedient (native refuses master and follow without learned Obedience), is
// marked for release or slaughter, has an unread fact, or its race is
// unplanned or retiring. A master who fits is kept and reassigned only when
// empty or not fitting. One write per call: master first, then follow_drafted.
func HerdMasterChoice(animals domain.Fact[[]UpkeepAnimal], herd HerdPolicy, profiles domain.Fact[[]PawnProfile]) HusbandryChoice {
	none := HusbandryChoice{Reason: HusbandryNoDeficit}
	rows, known := animals.Value()
	roster, rosterKnown := profiles.Value()
	if !known || !rosterKnown {
		return none
	}
	rows = append([]UpkeepAnimal(nil), rows...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
	front, _ := FrontLine(roster)
	for _, a := range rows {
		role, planned := herd.Roles[a.Definition]
		if !planned || role.Retiring {
			continue
		}
		obedient, ok := a.Obedient.Value()
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		master, mk := a.Master.Value()
		if !ok || !obedient || !rk || !sk || release || slaughter || !mk {
			continue
		}
		fit, best, found := bondedMasters(roster, a.BondedPawns)
		war := false
		if !found {
			training, known := herdUnlearned(herd, a)
			if !known {
				continue
			}
			switch {
			case training:
				fit = herdMasters(roster, nil)
			case role.Job == HerdJobWar:
				fit, war = herdMasters(roster, front), true
			default:
				continue
			}
		}
		if !fit[PawnID(master)] {
			if !found {
				best, found = herdBestMaster(roster, fit)
			}
			if !found {
				continue
			}
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryMaster, Argument: string(best)}
		}
		if drafted, dk := a.FollowDrafted.Value(); war && dk && !drafted {
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryFollowDrafted, Argument: "true"}
		}
	}
	return none
}

// herdUnlearned reports whether the animal has a wanted, available trainable
// it has not learned; known is false when one of those facts is unread.
func herdUnlearned(herd HerdPolicy, a UpkeepAnimal) (unlearned, known bool) {
	for _, t := range herdTrainQueue(herd, a) {
		avail, ak := t.Available.Value()
		learned, lk := t.Learned.Value()
		if !ak || avail && !lk {
			return false, false
		}
		if avail && !learned {
			unlearned = true
		}
	}
	return unlearned, true
}

// herdMasters are the handling-capable colonists, limited to only when it is
// not nil (the front line for a war animal).
func herdMasters(roster []PawnProfile, only []PawnID) map[PawnID]bool {
	allowed := map[PawnID]bool{}
	for _, id := range only {
		allowed[id] = true
	}
	fit := map[PawnID]bool{}
	for _, p := range roster {
		if p.Capable(WorkHandling, 0) && (only == nil || allowed[p.ID]) {
			fit[p.ID] = true
		}
	}
	return fit
}

// herdBestMaster is the fitting colonist with the highest Animals skill.
func herdBestMaster(roster []PawnProfile, fit map[PawnID]bool) (PawnID, bool) {
	var candidates []roleCandidate
	for _, p := range roster {
		if fit[p.ID] {
			candidates = append(candidates, roleCandidate{p.ID, float64(p.SkillFor(WorkHandling).Level)})
		}
	}
	return bestRole(candidates)
}

// bondedMasters is the set of an animal's bond partners on the roster and
// the first of them by id.
func bondedMasters(roster []PawnProfile, bonded []string) (map[PawnID]bool, PawnID, bool) {
	onRoster := map[PawnID]bool{}
	for _, p := range roster {
		onRoster[p.ID] = true
	}
	fit := map[PawnID]bool{}
	var first PawnID
	for _, id := range bonded {
		if pid := PawnID(id); onRoster[pid] {
			fit[pid] = true
			if first == "" || pid < first {
				first = pid
			}
		}
	}
	return fit, first, first != ""
}
