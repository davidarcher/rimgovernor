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
