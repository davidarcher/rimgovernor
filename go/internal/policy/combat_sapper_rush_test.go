package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// brawlerAttack is brawler m's attack order, if any.
func brawlerAttack(orders []CombatOrder) (CombatOrder, bool) {
	for _, o := range orders {
		if o.Pawn == "m" && o.Kind == OrderAttack {
			return o, true
		}
	}
	return CombatOrder{}, false
}

// {sappers 14 cells from the breach} -> the brawler holds, no attack.
func TestDecideCombatSapperBrawlersHoldUntilBreach(t *testing.T) {
	orders, m := decideStop(t, sapperView(), StopEvent{}, CombatMemory{})
	if o, ok := brawlerAttack(orders); ok || m.Rushing {
		t.Fatalf("%+v %+v", o, m)
	}
}

// {a sapper 2 cells from the breach} -> the brawler attacks it.
func TestDecideCombatSapperBrawlersRushThroughBreach(t *testing.T) {
	_, m := decideStop(t, sapperView(), StopEvent{}, CombatMemory{})
	view := withBrawlers(holdView(), combatBrawler("m", 0.5))
	view.Rooms = []CombatRoom{sapperRoom}
	view = withSappers(view, map[PawnID]domain.Cell{"r1": {X: 10, Z: 17}, "r2": {X: 10, Z: 4}})
	// The brawler stands at its post.
	for i, p := range view.Pawns {
		if p.ID == "m" {
			view.Pawns[i].Cell = domain.Known(domain.Cell{X: 10, Z: 20})
		}
	}
	orders, next := decideStop(t, view, StopEvent{}, m)
	if o, ok := brawlerAttack(orders); !ok || o.Target != "r1" || !next.Rushing {
		t.Fatalf("%+v %+v", orders, next)
	}
}

// {breach stop} -> the brawler attacks the hostile nearest the breach.
func TestDecideCombatSapperBreachStopRushes(t *testing.T) {
	_, m := decideStop(t, sapperView(), StopEvent{}, CombatMemory{})
	orders, next := decideStop(t, sapperView(), StopEvent{Kind: StopBreach}, m)
	if o, ok := brawlerAttack(orders); !ok || o.Target != "r1" || !next.Rushing {
		t.Fatalf("%+v %+v", orders, next)
	}
}
