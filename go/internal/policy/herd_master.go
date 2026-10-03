package policy

import (
	"sort"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HerdMasterChoice assigns one animal a master and follow flags by its race's
// job (#1635): a war animal is mastered by the best front-line handler and
// follows when drafted, a haul animal by the best hauler and follows
// fieldwork; the other flag is written off. An animal is skipped while it is
// not obedient (native refuses master and follow without learned Obedience),
// is marked for release or slaughter, has any unread fact, or its race has no
// work job in the plan. An animal keeps a master who still fits the job and
// is reassigned only when the master is empty or does not (the working
// agreement lets Auto override a player's choice). One write per call:
// master first, then follow_drafted, then follow_fieldwork.
//
// Companions are left unmastered: their master is the bonded colonist, and
// AnimalState carries no bond partner id yet (#1635).
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
		role := herd.Roles[a.Definition]
		if role.Retiring || role.Job != HerdJobWar && role.Job != HerdJobHaul {
			continue
		}
		obedient, ok := a.Obedient.Value()
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		master, mk := a.Master.Value()
		drafted, dk := a.FollowDrafted.Value()
		fieldwork, fk := a.FollowFieldwork.Value()
		if !ok || !obedient || !rk || !sk || release || slaughter || !mk || !dk || !fk {
			continue
		}
		wantDrafted, wantFieldwork := role.Job == HerdJobWar, role.Job == HerdJobHaul
		fit := herdMasters(roster, front, role.Job)
		if !fit[PawnID(master)] {
			best, found := herdBestMaster(roster, fit)
			if !found {
				continue
			}
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryMaster, Argument: string(best)}
		}
		if drafted != wantDrafted {
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryFollowDrafted, Argument: strconv.FormatBool(wantDrafted)}
		}
		if fieldwork != wantFieldwork {
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryFollowFieldwork, Argument: strconv.FormatBool(wantFieldwork)}
		}
	}
	return none
}

// herdMasters are the handling-capable colonists who fit a job's master:
// front-line pawns for war, hauling-capable pawns for haul.
func herdMasters(roster []PawnProfile, front []PawnID, job HerdJob) map[PawnID]bool {
	inFront := map[PawnID]bool{}
	for _, id := range front {
		inFront[id] = true
	}
	fit := map[PawnID]bool{}
	for _, p := range roster {
		if !p.Capable(WorkHandling, 0) {
			continue
		}
		if job == HerdJobWar && inFront[p.ID] || job == HerdJobHaul && p.Capable(WorkHauling, 0) {
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
			candidates = append(candidates, roleCandidate{p.ID, float64(p.Skill("Animals").Level)})
		}
	}
	return bestRole(candidates)
}
