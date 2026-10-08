package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

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
	QuestRefugee                                     bool
	QuestProtected                                   bool
	WillJoinIfRescued                                domain.Fact[bool]
	Pawn                                             domain.PawnID
	Dead, Downed, Guest, Admitted, Prisoner, Hostile domain.Fact[bool]
	// Recruitable is the game's guest.Recruitable, rolled at pawn generation
	// (PawnGenerator.GeneratePawn -> SetupRecruitable), so a downed raider
	// already carries it before capture. It orders captures only (#1034).
	Recruitable domain.Fact[bool]
	// Prospect is the pawn's biography: skills rank a standing hostile as
	// a lance target (#1038).
	Prospect domain.Fact[PrisonerProspect]
	// Luciferium is a LuciferiumAddiction hediff (#1079): the raider is
	// not CaptureWorthy, so the fight strips and finishes it instead.
	// WearingApparel gates a capture on the fight's strip: every downed
	// raider is stripped before it is taken.
	Luciferium, WearingApparel domain.Fact[bool]
}

// CustodyPlanReason names why RoundsPopulationCustodyPlanner did or did
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
	if row.QuestRefugee && !admitted && !prisoner {
		if hostile {
			return CustodyCapture, true
		}
		return CustodyRescue, true
	}
	if !row.QuestProtected && !guest && !admitted && !prisoner && hostile {
		addicted, _ := row.Luciferium.Value()
		if worn, known := row.WearingApparel.Value(); !CaptureWorthy(CombatPawnState{Luciferium: addicted}) || !known || worn {
			return "", false
		}
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

// SelectCustodyMethod picks the next eligible candidate to dispatch. A
// capture of a raider not known to be recruitable ranks after every other
// candidate (#1034); within a rank the lowest pawn ID wins, mirroring
// SelectPrisonerInteractionMethod's determinism. Rescues are never demoted,
// and an unrecruitable raider is still captured once nothing outranks it.
func SelectCustodyMethod(rows domain.Fact[[]CustodyFacts]) CustodyChoice {
	all, known := rows.Value()
	if !known {
		return CustodyChoice{Reason: CustodyUnknown}
	}
	best, bestRank, found := CustodyChoice{}, 0, false
	for _, row := range all {
		decision, ok := custodyEligible(row)
		if !ok {
			continue
		}
		rank := 0
		if recruitable, known := row.Recruitable.Value(); decision == CustodyCapture && (!known || !recruitable) {
			rank = 1
		}
		if !found || rank < bestRank || (rank == bestRank && row.Pawn < best.Pawn) {
			best, bestRank, found = CustodyChoice{Pawn: row.Pawn, Decision: decision}, rank, true
		}
	}
	if !found {
		return CustodyChoice{Reason: CustodyNoDeficit}
	}
	return best
}
