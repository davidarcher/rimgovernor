package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainPopulation names Population-*'s prisoner interaction deficit: any
// observed, living colony prisoner that is recruitable but not yet set to
// the Recruit interaction, and -- only once an operator has opted in with a
// PrisonerPolicy.ReleaseAfterDays -- any prisoner the colony cannot turn
// (recruit resistance unbroken, or never recruitable) after that many days
// in custody while the colony's food runway sits below its target, not yet
// set to Release. Disclosed narrowing, mirroring MaintainHerd's: only the
// Recruit and Release writes are ever proposed; execution, enslavement,
// conversion and every other redirection remain player-only orders through
// SetPopulationDecision. Native eligibility (recruitable, alive, prisoner,
// not a wild man barred from the mode) is still re-validated by
// EvaluatePrisonerInteraction immediately before dispatch; this only decides
// which already-observed candidate to try.
const MaintainPopulation GoalID = "MaintainPopulation"

// PrisonerPolicy is the operator-declared slice of RoutinePolicy
// MaintainPopulation plans from. ReleaseAfterDays is the custody length,
// in days, past which a prisoner with unbroken resistance (or no recruit
// eligibility at all) is released rather than fed indefinitely; zero (the
// default) never proposes a release. FoodTargetDays is the colony food
// runway target the release path is gated under: a colony at or above its
// target keeps feeding the prisoner.
type PrisonerPolicy struct {
	ReleaseAfterDays float64
	FoodTargetDays   float64
}

// PrisonerPlanReason names why RoutinePrisonerInteractionPlanner did or did
// not propose an interaction write.
type PrisonerPlanReason string

const (
	PrisonerNoDeficit PrisonerPlanReason = "no_recruitable_prisoner"
	PrisonerUnknown   PrisonerPlanReason = "population_census_unknown"
)

// PrisonerChoice is the one prisoner/mode pair chosen for an interaction
// write, mirroring HusbandryChoice's single-write-per-cycle rule. Interaction
// is set whenever Reason is empty.
type PrisonerChoice struct {
	Reason      PrisonerPlanReason
	Pawn        domain.PawnID
	Interaction domain.PrisonerInteractionMode
}

// prisonerWant is the one write a living prisoner still needs, or "" when
// the row is settled. Release takes precedence over recruit: a prisoner
// past the release threshold is leaving whatever interaction is set. unknown
// reports a fact the decision needed but the census did not carry; an
// unknown fact is never evidence of a settled prisoner.
func prisonerWant(row PrisonerFacts, food domain.Fact[float64], p PrisonerPolicy) (want domain.PrisonerInteractionMode, unknown bool) {
	dead, dk := row.Dead.Value()
	if !dk {
		return "", true
	}
	if dead {
		return "", false
	}
	recruitable, rk := row.Recruitable.Value()
	if !rk {
		return "", true
	}
	current, ck := row.CurrentInteraction.Value()
	if !ck {
		return "", true
	}
	if p.ReleaseAfterDays > 0 {
		release, unknown := prisonerReleaseDue(row, recruitable, food, p)
		if unknown {
			return "", true
		}
		if release {
			if current == domain.PrisonerInteractionRelease {
				return "", false
			}
			return domain.PrisonerInteractionRelease, false
		}
	}
	if recruitable && current != domain.PrisonerInteractionRecruit {
		return domain.PrisonerInteractionRecruit, false
	}
	return "", false
}

// prisonerReleaseDue is the release rule for one living prisoner: held at
// least ReleaseAfterDays (60000 ticks per day), not turnable (never
// recruitable, or recruit resistance still above zero) and the colony's
// food runway below FoodTargetDays.
func prisonerReleaseDue(row PrisonerFacts, recruitable bool, food domain.Fact[float64], p PrisonerPolicy) (due, unknown bool) {
	held, hk := row.HeldTicks.Value()
	if !hk {
		return false, true
	}
	if float64(held) < p.ReleaseAfterDays*60000 {
		return false, false
	}
	turnable := recruitable
	if recruitable {
		resistance, rsk := row.Resistance.Value()
		if !rsk {
			return false, true
		}
		turnable = resistance <= 0
	}
	if turnable {
		return false, false
	}
	days, fk := food.Value()
	if !fk {
		return false, true
	}
	return days < p.FoodTargetDays, false
}

// PrisonerRecruitDeficit reports whether any observed, living prisoner
// still needs an interaction write under the policy. An unknown census, or
// any prisoner whose facts are incomplete for the decision, leaves the whole
// need unknown rather than silently treating it as recovered.
func PrisonerRecruitDeficit(prisoners domain.Fact[[]PrisonerFacts], food domain.Fact[float64], p PrisonerPolicy) domain.Fact[bool] {
	rows, known := prisoners.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	deficit := false
	for _, row := range rows {
		want, unknown := prisonerWant(row, food, p)
		if unknown {
			return domain.Unknown[bool]()
		}
		if want != "" {
			deficit = true
		}
	}
	return domain.Known(deficit)
}

// SelectPrisonerInteractionMethod picks the lowest-pawn-ID living prisoner
// still needing a write, and the write it needs, to dispatch next. A row
// whose facts are incomplete is skipped rather than blocking the rest.
func SelectPrisonerInteractionMethod(prisoners domain.Fact[[]PrisonerFacts], food domain.Fact[float64], p PrisonerPolicy) PrisonerChoice {
	rows, known := prisoners.Value()
	if !known {
		return PrisonerChoice{Reason: PrisonerUnknown}
	}
	sorted := append([]PrisonerFacts{}, rows...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Pawn < sorted[j].Pawn })
	for _, row := range sorted {
		if want, unknown := prisonerWant(row, food, p); !unknown && want != "" {
			return PrisonerChoice{Pawn: row.Pawn, Interaction: want}
		}
	}
	return PrisonerChoice{Reason: PrisonerNoDeficit}
}
