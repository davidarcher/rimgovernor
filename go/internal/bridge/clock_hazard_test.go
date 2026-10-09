package bridge

import (
	"errors"
	"math"
	"testing"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// Go sends the current hazard literals, and a policy missing or corrupting any
// one is refused before it reaches native.
func TestClockPolicyHazardThresholds(t *testing.T) {
	p := clockTestPolicy()
	want := map[string]float32{"single hit": 20, "summary health": 0.5, "bleed rate": 1, "vital part": 0.5, "explosive margin": 3, "melee reach": 1.5, "predator margin": 25}
	got := map[string]float32{"single hit": p.GetSeriousSingleHitDamage(), "summary health": p.GetSeriousSummaryHealthFloor(), "bleed rate": p.GetSeriousBleedRateFloor(),
		"vital part": p.GetSeriousVitalPartFloor(), "explosive margin": p.GetExplosiveNearMarginCells(), "melee reach": p.GetMeleeReachCells(), "predator margin": p.GetPredatorMarginCells()}
	for name, v := range want {
		if got[name] != v {
			t.Fatal(name, got[name], v)
		}
	}
	if p.GetInjurySeverityFloorTicks() != 5000 {
		t.Fatal(p.GetInjurySeverityFloorTicks())
	}
	if err := clockPolicy(p, 300); err != nil {
		t.Fatal(err)
	}
	for name, mutate := range map[string]func(*k.WatchPolicy){
		"no single hit":    func(p *k.WatchPolicy) { p.SeriousSingleHitDamage = nil },
		"no summary":       func(p *k.WatchPolicy) { p.SeriousSummaryHealthFloor = nil },
		"no bleed":         func(p *k.WatchPolicy) { p.SeriousBleedRateFloor = nil },
		"no vital":         func(p *k.WatchPolicy) { p.SeriousVitalPartFloor = nil },
		"no explosive":     func(p *k.WatchPolicy) { p.ExplosiveNearMarginCells = nil },
		"no melee":         func(p *k.WatchPolicy) { p.MeleeReachCells = nil },
		"no floor ticks":   func(p *k.WatchPolicy) { p.InjurySeverityFloorTicks = nil },
		"no predator":      func(p *k.WatchPolicy) { p.PredatorMarginCells = nil },
		"zero single hit":  func(p *k.WatchPolicy) { p.SeriousSingleHitDamage = proto.Float32(0) },
		"nan melee":        func(p *k.WatchPolicy) { p.MeleeReachCells = proto.Float32(float32(math.NaN())) },
		"inf predator":     func(p *k.WatchPolicy) { p.PredatorMarginCells = proto.Float32(float32(math.Inf(1))) },
		"zero floor ticks": func(p *k.WatchPolicy) { p.InjurySeverityFloorTicks = proto.Uint32(0) },
	} {
		v := proto.Clone(p).(*k.WatchPolicy)
		mutate(v)
		if err := clockPolicy(v, 300); !errors.Is(err, ErrContract) {
			t.Fatal(name, err)
		}
	}
}

// Go supplies the predator margin on every hunt census request; native has none.
func TestHuntRouteRequestCarriesPredatorMargin(t *testing.T) {
	if q := colonyFactsRequest(&c.Identity{}, true); q.HuntPredatorMarginCells == nil || q.GetHuntPredatorMarginCells() != 25 {
		t.Fatal(q)
	}
}
