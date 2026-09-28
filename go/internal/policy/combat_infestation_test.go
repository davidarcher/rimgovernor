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

// molotovHive is an infestation with a molotov carrier "a" on the line and
// the hive in its reach, the hive at temp.
func molotovHive(tick domain.Tick, temp float64) CombatView {
	view := infested(grenadeView("Weapon_GrenadeMolotov"))
	for i := range view.Pawns {
		if view.Pawns[i].ID[0] == 'h' {
			view.Pawns[i].Kind = "Megascarab"
		}
	}
	view.Tick, view.HiveTemperatureC = tick, domain.Known(temp)
	return view
}

// throwsAtHive reports a's molotov role aimed at the hive.
func throwsAtHive(m CombatMemory) bool {
	for _, r := range m.Roles {
		if r.Pawn == "a" && r.Ground != nil && *r.Ground == hiveCell {
			return true
		}
	}
	return false
}

// {a molotov carrier, the hive heating} -> molotovs until the hive reads
// 150 C, still inside 150-200 C, none above 200 C, and none once the hive
// has held 150 C for heatStrokeTicks (the insects past 60% heat stroke).
func TestHeatStrokeHold(t *testing.T) {
	var m CombatMemory
	for _, step := range []struct {
		tick  domain.Tick
		temp  float64
		throw bool
	}{{100, 30, true}, {200, 160, true}, {1200, 210, false}, {2200, 180, true}, {2200 + heatStrokeTicks, 170, false}} {
		_, m = decideStop(t, molotovHive(step.tick, step.temp), StopEvent{}, m)
		if m.Tactic != TacticInfestation {
			t.Fatalf("%+v", m)
		}
		if got := throwsAtHive(m); got != step.throw {
			t.Fatalf("tick %d at %v C: throw %v, want %v: %+v", step.tick, step.temp, got, step.throw, m.Roles)
		}
	}
	if m.HeatTicks < heatStrokeTicks {
		t.Fatalf("held %d ticks", m.HeatTicks)
	}
}

// {brawlers committed to an infestation} -> above 50 C at the hive no
// melee fighter is sent at an insect or the hive; at 30 C they charge.
func TestNoEntryWhenHot(t *testing.T) {
	for _, tc := range []struct {
		temp   float64
		charge bool
	}{{30, true}, {120, false}} {
		view := infested(withBrawlers(holdView(), combatBrawler("d", .3), combatBrawler("e", .9)))
		view.HiveTemperatureC = domain.Known(tc.temp)
		orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
		melee := map[domain.PawnID]bool{}
		for _, r := range m.Roles {
			melee[r.Pawn] = !r.Ranged
		}
		charged := false
		for _, o := range orders {
			charged = charged || melee[o.Pawn] && o.Kind == OrderAttack
		}
		if charged != tc.charge {
			t.Fatalf("%v C: charged %v: %+v", tc.temp, charged, orders)
		}
	}
}
