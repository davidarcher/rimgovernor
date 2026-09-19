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
func routineDiseaseDemand(facts observation.ColonyProjection, building bool, previous store.RoutineReview, current domain.GenerationSnapshot) (policy.WorkDemand, error) {
	demand := policy.RoutineWorkDemand(facts.Facts, building)
	history := previous.MedicalCare.Resting
	if previous.Snapshot.Colony != current.Colony || previous.Snapshot.Load != current.Load || previous.Snapshot.Map != current.Map || facts.Identity.Tick < previous.Tick {
		history = nil
	}
	var err error
	demand.Resting, err = policy.ReviewDiseaseRest(facts.Facts.MedicalPawns, history)
	return demand, err
}
