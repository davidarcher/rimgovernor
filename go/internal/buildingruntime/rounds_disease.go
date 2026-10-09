package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Both assessment and assignment use the same fresh disease projection. The
// journal owns the hold across restarts; world replacement and rewind discard
// it just as ReviewRounds discards the medical history.
//
// The same demand carries the construction helper input: the
// review's recorded ready work and helper record, and every open plan's
// building definition, so assessment and assignment plan the same helpers.
func roundsDiseaseDemand(facts observation.ColonyProjection, definitions []string, previous store.Rounds, current domain.GenerationSnapshot) (policy.WorkDemand, error) {
	demand := policy.RoundsWorkDemand(facts.Facts, len(definitions) > 0)
	history := previous.MedicalCare.Resting
	var helped *policy.ConstructionHelpRecord
	if previous.Roster != nil {
		helped = previous.Roster.Help
	}
	if previous.Snapshot.Colony != current.Colony || previous.Snapshot.Load != current.Load || previous.Snapshot.Map != current.Map || facts.Identity.Tick < previous.Tick {
		history, helped = nil, nil
	}
	help := policy.ConstructionHelpDemand(previous.ReadyWork, current, facts.Identity.Tick, helpDefinitions(definitions, facts.Definitions), helped, facts.Facts.CurrentConstruction)
	demand.Help = &help
	var err error
	demand.Resting, err = policy.ReviewDiseaseRest(facts.Facts.MedicalPawns, history)
	return demand, err
}

// helpDefinitions pairs each open plan's building definition with its
// observed skill prerequisite; a definition the catalog did not read keeps an
// unknown prerequisite.
func helpDefinitions(names []string, catalog []observation.PlanningDefinition) []policy.HelpDefinition {
	skills := map[string]domain.Fact[int32]{}
	for _, d := range catalog {
		skills[d.Name] = d.ConstructionSkill
	}
	out := make([]policy.HelpDefinition, 0, len(names))
	for _, name := range names {
		skill := domain.Unknown[int]()
		if v, ok := skills[name].Value(); ok {
			skill = domain.Known(int(v))
		}
		out = append(out, policy.HelpDefinition{Name: name, Skill: skill})
	}
	return out
}
