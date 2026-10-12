package combatlab

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// TestCalibrationColonistsAreTheMetricsCrew pins #2906. The real runs the gate
// is scored against are the metrics cases, which staff the lab with the
// superpawn crew (every skill at na.CrewSkillLevel) after the staging set
// level 8, so the calibration sim's colonists must shoot at the crew's level
// while the raiders keep the staging's. At level 8 the sim's colonists aimed
// on target at distance 25 about 23% of the time; the real crew did about 48%.
func TestCalibrationColonistsAreTheMetricsCrew(t *testing.T) {
	for _, name := range Names {
		c, ok := cases.Lookup("combatlab/metrics-" + name)
		if !ok || c.Crew.Ordinary || c.Crew.Size == 0 {
			t.Fatalf("combatlab/metrics-%s no longer staffs the lab with the crew (%+v): CalibrationEnv's colonist skill is the crew's only while it does", name, c.Crew)
		}
	}
	cx, cz := labCenter()
	fight, err := importFight(recordedcatalog.Catalog(t), "lab-ranged", cx, cz)
	if err != nil {
		t.Fatal(err)
	}
	aim := map[string]float64{}
	for _, p := range fight.Scenario.Pawns {
		if p.Combat == nil {
			continue
		}
		acc, known := p.Combat.Shooter.ShootingAccuracyPawn.Value()
		if !known {
			t.Fatalf("%s: shooting accuracy unknown", p.ID)
		}
		// AimOnTargetChance_StandardTarget at 25 cells against a body size 1
		// target in the open: accuracy^25 times the rifle's medium band.
		aim[p.Side] = math.Pow(acc, 25) * p.Combat.Weapon.AccuracyMedium
	}
	if got := aim[combatmetrics.Colonist]; math.Abs(got-0.51) > 0.02 {
		t.Errorf("crew colonist aim chance at 25 cells = %.3f, want about 0.51 (skill 20)", got)
	}
	if got := aim[combatmetrics.Hostile]; math.Abs(got-0.234) > 0.01 {
		t.Errorf("raider aim chance at 25 cells = %.3f, want about 0.234 (skill 8)", got)
	}
}
