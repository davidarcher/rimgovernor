package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Turret budgets scale with observed raid points; unknown threat keeps the
// base budget. Power and stock gates decide how many turrets can be placed.
const (
	turretBaseBudget = 2
	turretMidBudget  = 4
	turretHardCap    = 6
	// turretMidPoints and turretHighPoints are the raid-point thresholds
	// at which the budget steps up.
	turretMidPoints  = 300
	turretHighPoints = 800
)

// TurretBudget is the most turrets the defense layout proposes for the
// observed raid points: unknown or below turretMidPoints keeps the base
// budget (a NaN reading counts as unknown), below turretHighPoints doubles
// it, and anything above fills the hard cap.
func TurretBudget(raidPoints domain.Fact[float64]) int {
	points, known := raidPoints.Value()
	switch {
	case !known || math.IsNaN(points) || points < turretMidPoints:
		return turretBaseBudget
	case points < turretHighPoints:
		return turretMidBudget
	}
	return turretHardCap
}
