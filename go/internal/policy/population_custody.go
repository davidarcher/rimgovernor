package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CustodyDecision names which of Population-*'s two custody sub-steps a
// candidate calls for: capture (a downed hostile not yet colony property)
// or rescue (a downed guest not yet admitted). The decision is derived
// purely from observed facts -- guest status vs. hostility --
// matching the deficit-detection style every other MaintainX vertical uses.
type CustodyDecision string

const (
	CustodyCapture CustodyDecision = "capture"
	CustodyRescue  CustodyDecision = "rescue"
)

// CustodyFacts describes one humanlike population-census row as a candidate
// custody target. It is sourced from the same per-cycle population census
// PrisonerFacts already reads (rimgovernor/observations_read_population),
// broadened to include every observed humanlike rather than only prisoners.
type CustodyFacts struct {
	Pawn                                             domain.PawnID
	Dead, Downed, Guest, Admitted, Prisoner, Hostile domain.Fact[bool]
}

// CustodyPlanReason names why RoutinePopulationCustodyPlanner did or did
// not propose a capture/rescue write.
type CustodyPlanReason string

const (
	CustodyNoDeficit CustodyPlanReason = "no_custody_candidate"
	CustodyUnknown   CustodyPlanReason = "population_census_unknown"
)

// CustodyChoice is the one candidate chosen for a capture/rescue dispatch,
// mirroring PrisonerChoice's single-write-per-cycle rule.
type CustodyChoice struct {
	Reason   CustodyPlanReason
	Pawn     domain.PawnID
	Decision CustodyDecision
}

func custodyEligible(row CustodyFacts) (CustodyDecision, bool) {
	dead, dk := row.Dead.Value()
	downed, wk := row.Downed.Value()
	if !dk || !wk || dead || !downed {
		return "", false
	}
	guest, gk := row.Guest.Value()
	admitted, ak := row.Admitted.Value()
	if !gk || !ak {
		return "", false
	}
	if guest && !admitted {
		return CustodyRescue, true
	}
	prisoner, pk := row.Prisoner.Value()
	hostile, hk := row.Hostile.Value()
	if !pk || !hk {
		return "", false
	}
	if !guest && !admitted && !prisoner && hostile {
		return CustodyCapture, true
	}
	return "", false
}

// CustodyDeficit reports whether any observed candidate currently qualifies
// for capture or rescue. An unknown census leaves the whole need unknown;
// an individual row with incomplete facts is simply not a candidate, the
// same lenient style SelectRescue's eligibility checks use, since the
// candidate pool here is the whole map population rather than a small
// trusted list.
func CustodyDeficit(rows domain.Fact[[]CustodyFacts]) domain.Fact[bool] {
	all, known := rows.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, row := range all {
		if _, ok := custodyEligible(row); ok {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// SelectCustodyMethod picks the lowest-pawn-ID eligible candidate to
// dispatch next, mirroring SelectPrisonerInteractionMethod's determinism.
func SelectCustodyMethod(rows domain.Fact[[]CustodyFacts]) CustodyChoice {
	all, known := rows.Value()
	if !known {
		return CustodyChoice{Reason: CustodyUnknown}
	}
	sorted := append([]CustodyFacts{}, all...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Pawn < sorted[j].Pawn })
	for _, row := range sorted {
		if decision, ok := custodyEligible(row); ok {
			return CustodyChoice{Pawn: row.Pawn, Decision: decision}
		}
	}
	return CustodyChoice{Reason: CustodyNoDeficit}
}
