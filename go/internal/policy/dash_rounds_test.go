package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestDashRoundsKeepHypothermiaUnderTheCap walks one colonist through reviews
// every 600 ticks at -50 C with 720 steel scattered as ten stacks, the way the
// facts evolve: the window is planned once and held as the persisted latch, the
// pawn goes out only while it is open, and a pawn still outside when it closes
// turns back. Hypothermia must stay under the cap throughout.
func TestDashRoundsKeepHypothermiaUnderTheCap(t *testing.T) {
	const speed, review = 4.6, 600
	distances := []float64{60, 90, 120, 150, 180, 210, 240, 270, 300, 330}
	collected := map[string]bool{}
	dist := map[string]float64{}
	var rows []LootItem
	for i, d := range distances {
		id := fmt.Sprintf("steel%d", i)
		dist[id] = d
		rows = append(rows, LootItem{Supply: StartingSupply{Thing: id, Definition: "Steel", Cell: domain.Cell{X: int32(i), Z: 1}},
			Forbidden: true, SafeToHaul: true, SafetyKnown: true, Count: 72, PathLength: knownF(d)})
	}
	growth := PublishedHypothermiaTuning.growth(-50, -10)
	sev, maxSev, away, dwell := 0.0, 0.0, 0.0, 0
	target, returning := "", false
	var held DashWindow
	for tick := domain.Tick(0); tick < 4*60000; tick += 60 {
		if tick%review == 0 {
			f := shelterFacts("")
			f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionColdSnap}})
			f.OutdoorTemperature, f.Hostiles = domain.Known(-50.0), domain.Known(int64(0))
			f.Items = ItemFacts{Market: map[Resource]float64{"Steel": 1.9}}
			var left []LootItem
			for _, r := range rows {
				if !collected[r.Supply.Thing] {
					left = append(left, r)
				}
			}
			f.EventLoot = domain.Known(left)
			f.Tick = tick
			pawns := domain.Known([]WorkPawn{{ID: "a", Available: domain.Known(true), Body: dashBody(-10, sev, speed*HypothermiaMovingFactor(sev))}})
			f.Dash = ReviewDash(held, f, pawns, tick, PublishedHypothermiaTuning)
			held = dashLatch(f.Dash)
			open := f.DashOpenFor("a")
			switch {
			case target == "" && open:
				w, _ := f.Dash.Value()
				target, returning = w.Trips[0].ID, false
			case target != "" && !open:
				returning = true
			}
		}
		if target != "" {
			sev += growth
			v := speed * HypothermiaMovingFactor(sev)
			switch {
			case returning || dwell == -1:
				away -= v
				if away <= 0 {
					if !returning {
						collected[target] = true
					}
					away, target, returning, dwell = 0, "", false, 0
				}
			case away < dist[target]:
				away += v
			default:
				if dwell += 60; dwell >= DashPickupTicks {
					dwell = -1
				}
			}
		} else {
			sev = max(0, sev-HypothermiaRecovery(sev))
		}
		maxSev = max(maxSev, sev)
	}
	if maxSev >= DashSeverityCap {
		t.Fatalf("hypothermia reached %.3f, the cap is %.2f", maxSev, DashSeverityCap)
	}
	if len(collected) < 5 {
		t.Fatalf("only %d of %d stacks collected", len(collected), len(distances))
	}
	t.Logf("collected %d of %d stacks, peak hypothermia %.3f", len(collected), len(distances), maxSev)
}
