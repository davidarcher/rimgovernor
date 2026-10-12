package combatsim

import (
	"math"
	"testing"
)

// TestAccuracyCurveInverts pins the postProcessCurve port: the stat values
// of the lab crew (raw 8 and 20) round-trip, and the curve rises.
func TestAccuracyCurveInverts(t *testing.T) {
	for _, raw := range []float64{-20, -3, 0, 4, 8, 11, 20, 45, 60} {
		if got := rawFromAccuracy(accuracyFromRaw(raw)); math.Abs(got-raw) > 1e-9 {
			t.Errorf("raw %v -> accuracy %v -> raw %v", raw, accuracyFromRaw(raw), got)
		}
	}
	if got := accuracyFromRaw(8); got != 0.96 {
		t.Errorf("accuracy at raw 8 = %v, want 0.96", got)
	}
	if got := accuracyFromRaw(100); got != 0.999 {
		t.Errorf("accuracy past the last point = %v, want 0.999", got)
	}
}

// TestInjuredShooterAimsWorse checks the capacity offsets: a pawn at full
// Manipulation and Sight keeps its read accuracy, a hurt one loses
// (min(level, max) - 1) x scale raw points: Manipulation 0.5 is -4, so raw 8
// (0.96) becomes raw 4 (0.94); a lost eye leaves Sight 0.75, -3 raw points.
func TestInjuredShooterAimsWorse(t *testing.T) {
	p := &pawn{}
	p.accHealthy = 0.96
	p.refreshAccuracy(capacities{manipulation: 1, sight: 1})
	if p.sh.acc != 0.96 {
		t.Errorf("healthy accuracy = %v", p.sh.acc)
	}
	p.refreshAccuracy(capacities{manipulation: 0.5, sight: 1})
	if math.Abs(p.sh.acc-0.94) > 1e-9 {
		t.Errorf("manipulation 0.5 accuracy = %v, want 0.94", p.sh.acc)
	}
	p.refreshAccuracy(capacities{manipulation: 1, sight: 0.75})
	if want := accuracyFromRaw(5); math.Abs(p.sh.acc-want) > 1e-9 {
		t.Errorf("sight 0.75 accuracy = %v, want %v", p.sh.acc, want)
	}
	p.refreshAccuracy(capacities{manipulation: 1, sight: 1})
	if p.sh.acc != 0.96 {
		t.Errorf("accuracy does not recover: %v", p.sh.acc)
	}
	// Sight above 1 is capped at 2 and raises accuracy; Manipulation is capped at 1.
	p.refreshAccuracy(capacities{manipulation: 1.5, sight: 1})
	if p.sh.acc != 0.96 {
		t.Errorf("manipulation above 1 moved accuracy: %v", p.sh.acc)
	}
}

// TestSightWeightsTheBestEye ports PawnCapacityWorker_Sight's special weight.
func TestSightWeightsTheBestEye(t *testing.T) {
	b := &Body{Parts: make([]BodyPart, 3), Sight: []int{1, 2}}
	for i := range b.Parts {
		b.Parts[i] = BodyPart{Parent: max(i-1, -1), MaxHealth: 10, HitWeight: 1}
	}
	st := newInjuryState(3)
	if got := b.sightEfficiency(&st); got != 1 {
		t.Errorf("healthy sight = %v", got)
	}
	st.missing[2] = true
	if got := b.sightEfficiency(&st); got != 0.75 {
		t.Errorf("one eye lost: sight = %v, want 0.75", got)
	}
}
