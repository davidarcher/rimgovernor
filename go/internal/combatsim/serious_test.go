package combatsim

import (
	"math"
	"testing"
)

// bleedingAnatomy is testAnatomy with bleeding on: every part bleeds at 1, an
// injury at 0.1 per severity, a lost part at 0.2 per point of part health.
func bleedingAnatomy() *Body {
	b := testAnatomy()
	b.BleedPerSeverity, b.MissingBleedPerSeverity, b.BleedFactor = 0.1, 0.2, 1
	for i := range b.Parts {
		b.Parts[i].BleedRate = 1
		b.Parts[i].HitPoints = b.Parts[i].MaxHealth
	}
	return b
}

func TestSummaryHealthIsTheProductOfPartImpacts(t *testing.T) {
	b := bleedingAnatomy()
	st := fresh(b)
	if got := b.summaryHealth(&st); got != 1 {
		t.Errorf("unhurt summary health %v, want 1", got)
	}
	b.addInjury(&st, pTorso, 15)
	b.addInjury(&st, pHead, 7.5)
	want := (1 - 15.0/75) * (1 - 7.5/75)
	if got := b.summaryHealth(&st); math.Abs(got-want) > 1e-9 {
		t.Errorf("summary health %v, want %v", got, want)
	}
}

func TestSummaryHealthCountsAFreshLostLimbByItsHitPoints(t *testing.T) {
	b := bleedingAnatomy()
	st := fresh(b)
	b.addInjury(&st, pArm, 35) // destroyed: 30 hit points, the hand goes with it
	want := 1 - 30.0/75
	if got := b.summaryHealth(&st); math.Abs(got-want) > 1e-9 {
		t.Errorf("summary health %v, want %v", got, want)
	}
}

func TestBleedRateSumsInjuriesAndSkipsSolidParts(t *testing.T) {
	b := bleedingAnatomy()
	st := fresh(b)
	b.addInjury(&st, pTorso, 10)
	b.addInjury(&st, pHeart, 5) // solid: does not bleed
	if got, want := b.bleedRate(&st), 10*0.1; math.Abs(got-want) > 1e-9 {
		t.Errorf("bleed rate %v, want %v", got, want)
	}
	b.BleedFactor = 0
	if got := b.bleedRate(&st); got != 0 {
		t.Errorf("a race that cannot bleed has bleed rate %v", got)
	}
}

func TestBleedRateOfALostLimbCountsOnce(t *testing.T) {
	b := bleedingAnatomy()
	st := fresh(b)
	b.addInjury(&st, pArm, 35)
	if got, want := b.bleedRate(&st), 30*0.2; math.Abs(got-want) > 1e-9 {
		t.Errorf("bleed rate %v, want %v (the hand under it adds none)", got, want)
	}
}

func TestSeriousHitTriggers(t *testing.T) {
	cases := []struct {
		name string
		e    Event
		want bool
	}{
		{"light hit", Event{Amount: 5, HealthBefore: 1, Health: 0.9}, false},
		{"single heavy hit", Event{Amount: 20, HealthBefore: 1, Health: 0.7}, true},
		{"health crosses the floor", Event{Amount: 8, HealthBefore: 0.52, Health: 0.48}, true},
		{"health was already under", Event{Amount: 8, HealthBefore: 0.4, Health: 0.35}, false},
		{"bleed crosses the floor", Event{Amount: 8, HealthBefore: 1, Health: 0.9, BleedBefore: 0.9, Bleed: 1.1}, true},
		{"bleed was already over", Event{Amount: 8, HealthBefore: 1, Health: 0.9, BleedBefore: 1.2, Bleed: 1.4}, false},
	}
	for _, c := range cases {
		if got := seriousHit(c.e); got != c.want {
			t.Errorf("%s: seriousHit = %v, want %v", c.name, got, c.want)
		}
	}
}
