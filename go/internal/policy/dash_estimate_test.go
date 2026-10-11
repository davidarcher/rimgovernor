package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func knownF(v float64) domain.Fact[float64] { return domain.Known(v) }

func TestOutdoorBudgetFor(t *testing.T) {
	tun := PublishedHypothermiaTuning
	cases := []struct {
		name      string
		temp, cmf float64
		sev, tgt  float64
		min, max  int
		unbounded bool
	}{
		{"death at 100 below", -90, 10, 0, HypothermiaDeath, 10200, 10400, false},
		{"dash under minor at -90 ambient", -90, 10, 0, HypothermiaMinor, 1000, 2400, false},
		{"mild cold unbounded", 5, 10, 0, HypothermiaMinor, 0, 0, true},
		{"edge of harm unbounded", 0, 10, 0, HypothermiaMinor, 0, 0, true},
		{"already at target", -90, 10, 0.2, HypothermiaMinor, 0, 0, false},
		{"minimum growth just below onset", -1, 10, 0, HypothermiaMinor, 15000, 16200, false},
	}
	for _, c := range cases {
		got, ok := OutdoorBudgetFor(HazardExtremeCold, knownF(c.temp), knownF(c.cmf), knownF(c.sev), c.tgt, tun).Value()
		if !ok {
			t.Fatalf("%s: unknown", c.name)
		}
		if got.Unbounded != c.unbounded || (!c.unbounded && (got.Ticks < c.min || got.Ticks > c.max)) {
			t.Errorf("%s: got %+v want [%d,%d] unbounded=%v", c.name, got, c.min, c.max, c.unbounded)
		}
	}
}

func TestOutdoorBudgetAccumulatedSeverityShrinksTrip(t *testing.T) {
	tun := PublishedHypothermiaTuning
	fresh, _ := OutdoorBudgetFor(HazardExtremeCold, knownF(-60), knownF(10), knownF(0), HypothermiaMinor, tun).Value()
	worn, _ := OutdoorBudgetFor(HazardExtremeCold, knownF(-60), knownF(10), knownF(0.1), HypothermiaMinor, tun).Value()
	if worn.Ticks >= fresh.Ticks || fresh.Ticks == 0 {
		t.Fatalf("fresh %d worn %d", fresh.Ticks, worn.Ticks)
	}
}

func TestOutdoorBudgetUnknowns(t *testing.T) {
	tun := PublishedHypothermiaTuning
	for _, k := range []HazardKind{HazardExtremeHeat, HazardToxicFallout} {
		if _, ok := OutdoorBudgetFor(k, knownF(50), knownF(10), knownF(0), 0.2, tun).Value(); ok {
			t.Errorf("%s should be unknown", k)
		}
	}
	if _, ok := OutdoorBudgetFor(HazardExtremeCold, domain.Unknown[float64](), knownF(10), knownF(0), 0.2, tun).Value(); ok {
		t.Error("unknown temperature should be unknown")
	}
}

func TestHypothermiaRecovery(t *testing.T) {
	for sev, want := range map[float64]float64{0.01: 0.0015, 0.2: 0.0054, 0.9: 0.015} {
		if got := HypothermiaRecovery(sev); got < want-1e-9 || got > want+1e-9 {
			t.Errorf("sev %v: %v want %v", sev, got, want)
		}
	}
}
