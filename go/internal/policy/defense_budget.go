package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Threat demand: the base budget plus turretStep more for every
// turretStepPoints of observed raid points. Unknown threat keeps the base
// budget. There is no ceiling; power, stock and the layout's geometry
// decide how many of the demanded turrets are placed, and each gate that
// stops placement is named on the tier.
const (
	turretBaseBudget = 2
	turretStep       = 2
	// turretStepPoints is the raid-point span that adds turretStep to the
	// demand.
	turretStepPoints = 300
	// turretHighPoints is the raid-point reading at which the mortar tier
	// opens (sieges are then a threat worth the shells).
	turretHighPoints = 800
)

// TurretBudget is the number of turrets the observed raid points demand:
// unknown, negative or NaN readings keep the base budget, and each full
// turretStepPoints adds turretStep.
func TurretBudget(raidPoints domain.Fact[float64]) int {
	points, known := raidPoints.Value()
	if !known || math.IsNaN(points) || math.IsInf(points, 0) || points < turretStepPoints {
		return turretBaseBudget
	}
	return turretBaseBudget + turretStep*int(points/turretStepPoints)
}
