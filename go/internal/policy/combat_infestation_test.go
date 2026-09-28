package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// hiveCell is the hive at the back of the mountain room, south of the line.
var hiveCell = domain.Cell{X: 9, Z: 12}

// infested makes view's raiders r1 and r2 megaspiders and puts a live hive
// at hiveCell.
func infested(view CombatView) CombatView {
	seen := map[domain.PawnID]bool{}
	for i := range view.Pawns {
		if id := view.Pawns[i].ID; id == "r1" || id == "r2" {
			view.Pawns[i].Kind, seen[id] = "Megaspider", true
		}
	}
	for _, t := range view.Positional {
		if id := domain.PawnID(t.ID); (id == "r1" || id == "r2") && !seen[id] {
			view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: t.Position, Kind: "Megaspider", Stance: StanceMoving})
		}
	}
	view.Structures = append(view.Structures, HostileStructure{ID: "Thing_Hive1", Def: "Hive", Cell: hiveCell})
	return view
}

// {insects, a hive, three riflemen and two brawlers} -> TacticInfestation,
// and every fighter gets an order at the first stop: none held back.
// Insects without a hive still classify; one raider among them does not.
func TestInfestationCommitsAll(t *testing.T) {
	view := infested(withBrawlers(holdView(), combatBrawler("d", .3), combatBrawler("e", .9)))
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticInfestation {
		t.Fatalf("%+v", m)
	}
	ordered := map[domain.PawnID]bool{}
	for _, o := range orders {
		ordered[o.Pawn] = true
	}
	for _, d := range view.Defenders {
		if !ordered[d.ID] {
			t.Fatalf("%s held back: %+v", d.ID, orders)
		}
	}
	bare := view
	bare.Structures = nil
	if !Infestation(bare) {
		t.Fatal("insects without a hive not an infestation")
	}
	s, d := combatRaider("r3", domain.Cell{X: 9, Z: 6})
	bare.Threats, bare.Positional = append(bare.Threats, s), append(bare.Positional, d)
	if Infestation(bare) {
		t.Fatal("a raider among the insects read as an infestation")
	}
}

// {a choke, four brawlers, insects} -> the best-armored brawlers block
// outside the choke with one in reserve, and a hurt blocker is relieved
// (#864).
func TestInfestationBlockRelief(t *testing.T) {
	view := infested(chokeView())
	_, memory := decideChoke(t, view, StopEvent{}, CombatMemory{})
	if memory.Tactic != TacticInfestation {
		t.Fatalf("%+v", memory)
	}
	duties := map[domain.PawnID]CombatDuty{}
	for _, r := range memory.Roles {
		duties[r.Pawn] = r.Duty
	}
	if duties["e"] != DutyBlocker || duties["f"] != DutyBlocker || duties["d"] != DutyBlocker || duties["g"] != DutyReserve {
		t.Fatalf("%+v", duties)
	}
	view.Tick = 400
	for i := range view.Pawns {
		if view.Pawns[i].ID == "e" {
			view.Pawns[i].Cell = domain.Known(domain.Cell{X: 9, Z: 18})
		}
	}
	orders, _ := decideChoke(t, view, StopEvent{Kind: StopSeriousInjury, Pawn: "e", Target: "r1"}, memory)
	relieved := false
	for _, o := range orders {
		relieved = relieved || o.Pawn == "g" && o.Kind == OrderMove && o.Cell == (domain.Cell{X: 9, Z: 18})
	}
	if !relieved {
		t.Fatalf("no relief: %+v", orders)
	}
}

// {our mortar and another hostile structure in range, a frag carrier on
// the line} -> no mortar order; the grenade goes at the hive.
func TestNoMortarUnderMountain(t *testing.T) {
	view := infested(grenadeView("Weapon_GrenadeFrag"))
	for i := range view.Pawns {
		if view.Pawns[i].ID[0] == 'h' {
			view.Pawns[i].Kind = "Megascarab"
		}
	}
	view.Mortars = []CombatMortar{{ID: "Thing_Turret_Mortar1", Cell: domain.Cell{X: 5, Z: 30}, MinRange: 5, MaxRange: 500}}
	view.Structures = append(view.Structures, HostileStructure{ID: "Thing_Turret_Mortar3", Def: "Turret_Mortar", Cell: domain.Cell{X: 9, Z: -20}})
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticInfestation {
		t.Fatalf("%+v", m)
	}
	if got := mortarOrders(orders); len(got) != 0 {
		t.Fatalf("mortar ordered: %+v", got)
	}
	thrown := false
	for _, o := range orders {
		thrown = thrown || o.Pawn == "a" && o.Kind == OrderAttackGround && o.Cell == hiveCell
	}
	if !thrown {
		t.Fatalf("no grenade at the hive: %+v", orders)
	}
}
