package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Both assessment and assignment use the same fresh disease projection. The
// journal owns the hold across restarts; world replacement and rewind discard
// it just as ReviewRoutine discards the medical history.
//
// The same demand carries the construction helper input (#653): the
// review's recorded ready work and helper record, and every open plan's
// building definition, so assessment and assignment plan the same helpers.
func routineDiseaseDemand(facts observation.ColonyProjection, definitions []string, previous store.RoutineReview, current domain.GenerationSnapshot) (policy.WorkDemand, error) {
	demand := policy.RoutineWorkDemand(facts.Facts, len(definitions) > 0)
	history := previous.MedicalCare.Resting
	var helped *policy.ConstructionHelpRecord
	if previous.Roster != nil {
		helped = previous.Roster.Help
	}
	if previous.Snapshot.Colony != current.Colony || previous.Snapshot.Load != current.Load || previous.Snapshot.Map != current.Map || facts.Identity.Tick < previous.Tick {
		history, helped = nil, nil
	}
	help := policy.ConstructionHelpDemand(previous.ReadyWork, current, facts.Identity.Tick, definitions, helped)
	demand.Help = &help
	var err error
	demand.Resting, err = policy.ReviewDiseaseRest(facts.Facts.MedicalPawns, history)
	return demand, err
}
