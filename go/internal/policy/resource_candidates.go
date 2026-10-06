package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type AcquisitionKind string

const (
	AcquisitionLoot    AcquisitionKind = "loot"
	AcquisitionSalvage AcquisitionKind = "salvage"
	AcquisitionMining  AcquisitionKind = "mining"
)

// AcquisitionYield is an estimated output, not credited inventory. Headroom is
// known destination capacity accepting this exact output, in item units.
type AcquisitionYield struct {
	ResourceQuantity
	UnitValue float64
	Headroom  domain.Fact[int64]
}

// AcquisitionCandidate is shared by loot, salvage and mining. Callers provide
// eligible candidates after reach/safety checks; ranking never authorizes an
// order. PathDistance is native path length, Labor is estimated work ticks.
// UnitsPerTrip is the observed carrying capacity for these outputs. Hauling
// can be omitted only for output consumed at the worksite.
type AcquisitionCandidate struct {
	ID           string
	Kind         AcquisitionKind
	Yields       []AcquisitionYield
	PathDistance domain.Fact[float64]
	Labor        domain.Fact[float64]
	NeedsHaul    bool
	UnitsPerTrip int64
}

type AcquisitionCompetition struct {
	// Zero means no competing urgent work. Routine deficits are not a veto.
	// An urgent priority above every matched demand holds this candidate.
	UrgentPriority int
}

func finiteAcquisitionCost(v float64) bool {
	return !math.IsNaN(v) && !math.IsInf(v, 0) && v >= 0 && v <= 1e12
}
