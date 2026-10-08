package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestFailedSiteFightWalksSurvivorsToNativeExit(t *testing.T) {
	exit := domain.Cell{X: 0, Z: 20}
	view := CombatView{Tick: 10, Pawns: []CombatPawnState{{ID: "a", Cell: domain.Known(domain.Cell{X: 10, Z: 20})}, {ID: "b", Downed: true}}, Orderable: []domain.PawnID{"a"}, SiteExit: domain.Known(CombatSiteExit{Crew: []domain.PawnID{"a", "b"}, Cells: []domain.Cell{exit}})}
	orders, ask, memory := DecideCombat(view, GeometryReply{}, StopEvent{Kind: "downed", Pawn: "b"}, CombatMemory{})
	if ask != nil || len(orders) != 1 || orders[0].Pawn != "a" || orders[0].Cell != exit || orders[0].Reason != ReasonRetreat || memory.Tactic != TacticSiteExit {
		t.Fatal(orders, ask, memory)
	}
	view.Tick++
	view.Pawns[0].Stance = StanceMoving
	orders, ask, memory = DecideCombat(view, GeometryReply{}, StopEvent{}, memory)
	if len(orders) != 0 || ask != nil || memory.Tactic != TacticSiteExit {
		t.Fatal(orders, ask, memory)
	}
	view.Pawns[0].Dead = true
	orders, _, memory = DecideCombat(view, GeometryReply{}, StopEvent{}, memory)
	if len(orders) != 0 || len(memory.Roles) != 0 {
		t.Fatal(orders, memory)
	}
}

func TestSiteExitNeedsNativeExitCellsAndFailedFight(t *testing.T) {
	view := CombatView{SiteExit: domain.Known(CombatSiteExit{Crew: []domain.PawnID{"a"}, Cells: []domain.Cell{{X: 0, Z: 20}}})}
	state := map[domain.PawnID]CombatPawnState{"a": {ID: "a"}}
	memory := CombatMemory{}
	if _, exit := siteExitTurn(view, StopEvent{}, &memory, state, map[domain.PawnID]bool{"a": true}); exit {
		t.Fatal("healthy fight retreated")
	}
	view.SiteExit = domain.Unknown[CombatSiteExit]()
	memory.Scattered = true
	if _, exit := siteExitTurn(view, StopEvent{}, &memory, state, map[domain.PawnID]bool{"a": true}); exit {
		t.Fatal("invented an exit")
	}
}
