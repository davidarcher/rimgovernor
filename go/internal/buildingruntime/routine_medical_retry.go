package buildingruntime

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// maxMedicalAttemptsPerPatient bounds repeated tend/rescue attempts for one
// patient within one goal episode (epoch). A patient who cannot be treated or
// rescued after this many tries stops consuming the goal's method slot rather
// than retrying forever; the deficit remains visible.
const maxMedicalAttemptsPerPatient = 8

// medicalAttemptCount counts prior same-episode methods whose ID has the given
// prefix. Method IDs are keyed by patient and attempt count, not by doctor or
// rescuer, so a fresh attempt after an interrupted or failed try naturally
// picks whichever candidate is currently best — this is how doctor/rescuer
// replacement happens, without a second bespoke recovery mechanism.
func medicalAttemptCount(methods []domain.GoalMethod, epoch uint64, prefix string) int {
	count := 0
	for _, m := range methods {
		if m.Epoch == epoch && strings.HasPrefix(string(m.Method), prefix) {
			count++
		}
	}
	return count
}

// medicalWaitTicks bounds one clock window lent when a standing CriticalMedical
// deficit has no method to run: no doctor/rescuer-patient pair the policy will
// select, or the per-patient attempts spent. The emergency freezes development
// (every goal "not selected: emergency") and the tend planner contributes no
// plan, so without a lent window the step reports no work and the clock parks
// on no_work for as long as the emergency stands -- an hour of wall time at a
// fixed tick in #636. What clears such a deficit is game time: a doctor
// finishing the job it is on, a patient reaching a bed, or the injury tending
// itself out. Lending the same bound the stock waits use keeps the world moving
// under the emergency without pretending a method ran.
const medicalWaitTicks = stockWaitTicks
