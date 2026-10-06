package policy

import "math"

type AcquisitionCompetition struct {
	// Zero means no competing urgent work. Routine deficits are not a veto.
	// An urgent priority above every matched demand holds this candidate.
	UrgentPriority int
}

func finiteAcquisitionCost(v float64) bool {
	return !math.IsNaN(v) && !math.IsInf(v, 0) && v >= 0 && v <= 1e12
}
