package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainPopulation names Population-*'s prisoner recruit deficit: any
// observed, living colony prisoner that is recruitable but not yet set to
// the Recruit interaction. Disclosed narrowing, mirroring MaintainHerd's:
// this only ever proposes the Recruit write, matching the native default of
// MaintainOnly -- a prisoner is only ever switched toward recruitment, never
// released, executed or otherwise redirected autonomously; those remain
// player-only orders through SetPopulationDecision (population.py has no
// autonomous equivalent at all, so there is no richer reference contract to
// preserve beyond this one safe direction). Native eligibility (recruitable,
// alive, prisoner, not a wild man barred from the mode) is still
// re-validated by EvaluatePrisonerInteraction immediately before dispatch;
// this only decides which already-observed candidate to try.
const MaintainPopulation GoalID = "MaintainPopulation"

// PrisonerPlanReason names why RoutinePrisonerInteractionPlanner did or did
// not propose a recruit-interaction write.
type PrisonerPlanReason string

const (
	PrisonerNoDeficit PrisonerPlanReason = "no_recruitable_prisoner"
	PrisonerUnknown   PrisonerPlanReason = "population_census_unknown"
)

// PrisonerChoice is the one prisoner chosen for a recruit-interaction write,
// mirroring HusbandryChoice's single-write-per-cycle rule.
type PrisonerChoice struct {
	Reason PrisonerPlanReason
	Pawn   domain.PawnID
}

// PrisonerRecruitDeficit reports whether any observed, living prisoner is
// recruitable but not yet set to the Recruit interaction. An unknown
// census, or any prisoner whose dead/recruitable/interaction facts are
// incomplete, leaves the whole need unknown rather than silently treating
// it as recovered.
func PrisonerRecruitDeficit(prisoners domain.Fact[[]PrisonerFacts]) domain.Fact[bool] {
	rows, known := prisoners.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	deficit := false
	for _, row := range rows {
		dead, dk := row.Dead.Value()
		if !dk {
			return domain.Unknown[bool]()
		}
		if dead {
			continue
		}
		recruitable, rk := row.Recruitable.Value()
		if !rk {
			return domain.Unknown[bool]()
		}
		if !recruitable {
			continue
		}
		current, ck := row.CurrentInteraction.Value()
		if !ck {
			return domain.Unknown[bool]()
		}
		if current != domain.PrisonerInteractionRecruit {
			deficit = true
		}
	}
	return domain.Known(deficit)
}

// SelectPrisonerInteractionMethod picks the lowest-pawn-ID recruitable,
// living prisoner not yet set to Recruit to dispatch next.
func SelectPrisonerInteractionMethod(prisoners domain.Fact[[]PrisonerFacts]) PrisonerChoice {
	rows, known := prisoners.Value()
	if !known {
		return PrisonerChoice{Reason: PrisonerUnknown}
	}
	sorted := append([]PrisonerFacts{}, rows...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Pawn < sorted[j].Pawn })
	for _, row := range sorted {
		if dead, dk := row.Dead.Value(); !dk || dead {
			continue
		}
		if recruitable, rk := row.Recruitable.Value(); !rk || !recruitable {
			continue
		}
		if current, ck := row.CurrentInteraction.Value(); ck && current != domain.PrisonerInteractionRecruit {
			return PrisonerChoice{Pawn: row.Pawn}
		}
	}
	return PrisonerChoice{Reason: PrisonerNoDeficit}
}
