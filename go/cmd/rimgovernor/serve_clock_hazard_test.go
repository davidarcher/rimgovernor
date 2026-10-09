package main

import "testing"

// The serve clock start policy sends every hazard threshold native applies; the
// values are the literals native used to hold.
func TestServiceClockConfigSendsHazardThresholds(t *testing.T) {
	p := serviceClockConfig("", false, 600, 0).Start.Policy
	for name, pair := range map[string][2]float32{
		"single hit":       {p.GetSeriousSingleHitDamage(), 20},
		"summary health":   {p.GetSeriousSummaryHealthFloor(), 0.5},
		"bleed rate":       {p.GetSeriousBleedRateFloor(), 1},
		"vital part":       {p.GetSeriousVitalPartFloor(), 0.5},
		"explosive margin": {p.GetExplosiveNearMarginCells(), 3},
		"melee reach":      {p.GetMeleeReachCells(), 1.5},
		"predator margin":  {p.GetPredatorMarginCells(), 25},
	} {
		if pair[0] != pair[1] {
			t.Fatal(name, pair[0], pair[1])
		}
	}
	if p.GetInjurySeverityFloorTicks() != 5000 {
		t.Fatal(p.GetInjurySeverityFloorTicks())
	}
}
