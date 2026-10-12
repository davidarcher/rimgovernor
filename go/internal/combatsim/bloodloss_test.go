package combatsim

import (
	"math"
	"testing"
)

func TestBloodStageBoundaries(t *testing.T) {
	for sev, want := range map[float64]int{0: 0, 0.1499: 0, 0.15: 1, 0.30: 2, 0.45: 3, 0.60: 4, 1: 4} {
		if got := bloodStage(sev); got != want {
			t.Errorf("bloodStage(%v) = %d, want %d", sev, got, want)
		}
	}
}

func TestBloodTickGrowsWithBleedingAndRecoversWithout(t *testing.T) {
	b := bleedingAnatomy()
	st := fresh(b)
	if b.bloodTick(&st) || st.blood != 0 {
		t.Errorf("an unhurt pawn changed blood to %v", st.blood)
	}
	st.blood = 0.0005
	b.bloodTick(&st) // no bleeding: recovers
	if want := 0.0005 - bloodLossRecovery; math.Abs(st.blood-want) > 1e-12 && st.blood != 0 {
		t.Errorf("recovered blood %v, want %v", st.blood, want)
	}
	st = fresh(b)
	b.addInjury(&st, pTorso, 10) // bleed 1.0
	b.bloodTick(&st)
	if want := 1.0 * bloodLossPerBleed; math.Abs(st.blood-want) > 1e-12 {
		t.Errorf("blood %v, want %v after one interval", st.blood, want)
	}
}

func TestBloodStageChangeReportsAndLowersConsciousness(t *testing.T) {
	b := bleedingAnatomy()
	st := fresh(b)
	base := b.levels(&st).consciousness
	st.blood = 0.1499
	if b.bloodTick(&st) {
		// a stage change needs the severity to cross 0.15; this one grows only when bleeding
		t.Errorf("stage change reported without bleeding")
	}
	st = fresh(b)
	b.addInjury(&st, pTorso, 10)
	st.blood = 0.1495
	if !b.bloodTick(&st) {
		t.Errorf("crossing 0.15 not reported (blood %v)", st.blood)
	}
	if got, want := b.levels(&st).consciousness, roundedHundredth(base-0.1); got != want {
		t.Errorf("consciousness %v at stage 1, want %v", got, want)
	}
	st.blood = 0.6
	if caps := b.levels(&st); caps.consciousness > 0.1 || !b.shouldBeDowned(&st, caps) {
		t.Errorf("the last stage must cap consciousness at 0.1 and down: %+v", caps)
	}
}

func TestBloodLossKillsAtLethalSeverity(t *testing.T) {
	b := bleedingAnatomy()
	st := fresh(b)
	b.addInjury(&st, pTorso, 10)
	st.blood = 0.9999
	if !b.bloodTick(&st) || st.blood != bloodLossLethal {
		t.Fatalf("blood %v, want clamped to lethal", st.blood)
	}
	if !b.shouldBeDead(&st, b.levels(&st)) {
		t.Errorf("lethal blood loss does not kill")
	}
}
