package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainIdeoRoles keeps the ideoligion's role precepts filled (#1661, epic
// #1653): each active role with a free place is given to the believer who
// best fits it, through the shared Assign intent (the role precept's id is
// the assignable thing). Which skills a role asks for, how many pawns it
// takes and when it is active are the catalog's role defs
// (IdeologyDefs.Roles); no role or skill name is listed here. A pawn that
// already holds a role is never moved.
const MaintainIdeoRoles ConcernID = "MaintainIdeoRoles"

// RoleAssignment is one pawn taking one role precept.
type RoleAssignment struct {
	Pawn PawnID
	// Role is the role precept's id (the thing Assign targets); Def its def.
	Role, Def string
}

// roleFit scores pawn for def: the sum, over the def's skill requirements,
// of the best level among the skills of that requirement. ok is false when
// the pawn fails a skill requirement (no listed skill is enabled and at the
// minimum). Requirements without skills are the game's to judge when the
// assignment applies.
func roleFit(def RoleDef, skills []WorkSkill) (score int, ok bool) {
	for _, requirement := range def.Requirements {
		if len(requirement.Skills) == 0 {
			continue
		}
		best, met := 0, false
		for _, need := range requirement.Skills {
			for _, s := range skills {
				if s.Name == need.Skill && !s.Disabled && s.Level >= need.MinLevel {
					met = true
					best = max(best, s.Level)
				}
			}
		}
		if !met {
			return 0, false
		}
		score += best
	}
	return score, true
}

// RoleAssignments are the assignments the ideoligion owes now: for each
// active role with fewer holders than its def's MaxCount, the best-fitting
// available believer who holds no role, by fit, then certainty, then id. The
// pawn row must show the ideoligion's id and no role; its skills must be
// read. Unknown without the ideoligion or the pawn rows; a role whose def is
// missing from the catalog fails the decode, so none reaches here. At most one
// pawn is chosen per role and per call, so a role with several places fills
// over successive calls as the section shows each holder. Sorted by role id.
func RoleAssignments(ideology domain.Fact[Ideoligion], pawns domain.Fact[[]WorkPawn]) domain.Fact[[]RoleAssignment] {
	ideo, ik := ideology.Value()
	rows, pk := pawns.Value()
	if !ik || !pk {
		return domain.Unknown[[]RoleAssignment]()
	}
	type candidate struct {
		id        PawnID
		skills    []WorkSkill
		certainty float64
	}
	var pool []candidate
	for _, row := range rows {
		available, ak := row.Available.Value()
		inputs, nk := row.PolicyInputs.Value()
		skills, sk := row.Skills.Value()
		certainty, _ := inputs.IdeoCertainty.Value()
		if ak && available && nk && sk && inputs.Ideo == ideo.Facts.IdeoID && inputs.IdeoRole == "" {
			pool = append(pool, candidate{row.ID, skills, certainty})
		}
	}
	roles := slices.Clone(ideo.Facts.Roles)
	sort.Slice(roles, func(i, j int) bool { return roles[i].ID < roles[j].ID })
	out := []RoleAssignment{}
	taken := map[PawnID]bool{}
	for _, role := range roles {
		def, ok := ideo.Defs.Roles[role.Def]
		if !ok || !role.Active || len(role.Pawns) >= def.MaxCount {
			continue
		}
		best, bestScore, found := candidate{}, 0, false
		for _, c := range pool {
			if taken[c.id] {
				continue
			}
			score, fits := roleFit(def, c.skills)
			if !fits {
				continue
			}
			if !found || score > bestScore || score == bestScore && (c.certainty > best.certainty || c.certainty == best.certainty && c.id < best.id) {
				best, bestScore, found = c, score, true
			}
		}
		if found {
			taken[best.id] = true
			out = append(out, RoleAssignment{Pawn: best.id, Role: role.ID, Def: role.Def})
		}
	}
	return domain.Known(out)
}

// RolesOwed measures MaintainIdeoRoles: known false when no role has a
// fitting believer waiting, unknown while the ideoligion or pawns are.
func RolesOwed(ideology domain.Fact[Ideoligion], pawns domain.Fact[[]WorkPawn]) domain.Fact[bool] {
	owed, ok := RoleAssignments(ideology, pawns).Value()
	if !ok {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(owed) > 0)
}
