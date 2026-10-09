package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// roundsMoodLedger builds the colony mood ledger from the review's mood
// census, each pawn's traits and precepts, and the catalog's ThoughtFacts. The
// ledger is unknown when the census is. A pawn attribute the snapshot lacks
// (an unread biography or policy block) stays unknown, and the expectation
// level is never read, so MinExpectation thoughts stay unverified.
func roundsMoodLedger(census domain.Fact[[]policy.MoodPawn], snapshot *o.PawnSnapshot, facts map[string]policy.ThoughtFacts) domain.Fact[policy.MoodLedger] {
	moods, known := census.Value()
	if !known {
		return domain.Unknown[policy.MoodLedger]()
	}
	byID := map[string]*o.PawnState{}
	for _, p := range snapshot.GetPawns() {
		byID[p.GetPawn().GetId()] = p
	}
	pawns := make([]policy.MoodLedgerPawn, 0, len(moods))
	for _, m := range moods {
		pawn := policy.MoodLedgerPawn{ID: m.ID, Thoughts: m.Thoughts, Traits: domain.Unknown[[]string](), Precepts: domain.Unknown[[]string](), Expectation: domain.Unknown[string]()}
		if r := byID[string(m.ID)]; r != nil {
			if b := r.Biography; b != nil && !hasIssue(b.Issues, "traits") {
				traits := []string{}
				for _, t := range b.Traits {
					traits = append(traits, t.GetDefName())
				}
				pawn.Traits = domain.Known(traits)
			}
			if s := r.Settings; s != nil && !hasIssue(s.Issues, "policy_inputs") {
				if inputs, ok := bridge.PawnPolicyInputs(s.PolicyInputs).Value(); ok {
					pawn.Precepts = domain.Known(append([]string{}, inputs.Precepts...))
				}
			}
		}
		pawns = append(pawns, pawn)
	}
	return domain.Known(policy.BuildMoodLedger(pawns, facts))
}
