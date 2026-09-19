package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The turret tier is the defense lever that scales with threat: the firing
// line is as long as the armed colonists, so raid points cannot lengthen
// it, but they can buy turrets (#396). The budget is a step function over
// the storyteller's raid points; an unknown reading keeps the pre-#341
// constant so the tier is unchanged until the observation lands. Power and
// stock gates still decide how many of the budget are placed.
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
