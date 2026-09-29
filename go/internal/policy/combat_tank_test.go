package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tankView is holdView (gunners on z=23 facing north, raiders to the
// south) with two shielded pawns: s (armor .8, a rifle it cannot fire
// through its belt) and t (armor .4, a melee weapon).
func tankView() CombatView {
	view := holdView()
	for i := range view.Threats {
		view.Threats[i].RangedEquipped = domain.Known(true)
	}
	s := combatRifleman("s")
	s.Armor = domain.Known(.8)
	view.Defenders = append(view.Defenders, s, combatBrawler("t", .4))
	for i, id := range []domain.PawnID{"s", "t"} {
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(domain.Cell{X: int32(5 + i), Z: 30}), Stance: StanceIdle, Shield: domain.Known(1.0)})
		view.Orderable = append(view.Orderable, id)
	}
	return view
}

// {shielded, armored pawns, gunner cells, approach} → each tank moves to
// the cell in front of a gunner, toward the raiders, best armor in front
// of the first gunner; the shielded rifleman is never a gunner.
func TestDecideCombatTanksStandBeforeGunners(t *testing.T) {
	orders, memory := decideStop(t, tankView(), StopEvent{}, CombatMemory{})
	move := func(p domain.PawnID, x, z int32) CombatOrder {
		return CombatOrder{Pawn: p, Kind: OrderMove, Cell: domain.Cell{X: x, Z: z}, Reason: ReasonFormation}
	}
	want := []CombatOrder{move("a", 9, 23), move("b", 8, 23), move("c", 10, 23), move("s", 9, 22), move("t", 8, 22)}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v\nwant %+v", orders, want)
	}
	for _, r := range memory.Roles {
		if (r.Pawn == "s" || r.Pawn == "t") && (r.Duty != DutyTank || r.Ranged) {
			t.Fatalf("%+v", r)
		}
	}
}

// {shield broken} → the tank moves back behind its gunner, a retreat that
// passes the aim guard; the other tank holds.
func TestDecideCombatTankPullsBackOnShieldBreak(t *testing.T) {
	view := tankView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	at := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}, "s": {X: 9, Z: 22}, "t": {X: 8, Z: 22}}
	for i := range view.Pawns {
		p := &view.Pawns[i]
		p.Cell, p.Stance = domain.Known(at[p.ID]), StanceCooldown
		if p.ID < "s" {
			p.Target = "r1"
		}
	}
	view.Pawns[3].Shield = domain.Known(0.0)
	view.Tick = 300
	orders, next := decideStop(t, view, StopEvent{Kind: StopShieldBroken, Pawn: "s"}, memory)
	want := []CombatOrder{{Pawn: "s", Kind: OrderMove, Cell: domain.Cell{X: 9, Z: 24}, Reason: ReasonRetreat}}
	if !reflect.DeepEqual(orders, want) || next.Tactic != TacticHold {
		t.Fatalf("%+v", orders)
	}
}

func tankCellsOf(m CombatMemory) map[domain.PawnID]domain.Cell {
	out := map[domain.PawnID]domain.Cell{}
	for _, r := range m.Roles {
		if r.Duty == DutyTank && r.Cell != nil {
			out[r.Pawn] = *r.Cell
		}
	}
	return out
}

// {front cell is cover, a covered cell ahead} → the tank takes the
// covered cell over nearer open ground (#1153).
func TestTankPrefersCoveredCellAhead(t *testing.T) {
	reply := GeometryReply{Scored: []ScoredCell{{Cell: domain.Cell{X: 10, Z: 22}, Lines: []CoverLine{{Hostile: "r1", Cover: .4, LineOfFire: true}}}}}
	_, memory := answerWithout(t, tankView(), reply, domain.Cell{X: 9, Z: 22})
	if got := tankCellsOf(memory); got["s"] != (domain.Cell{X: 10, Z: 22}) {
		t.Fatalf("%+v", got)
	}
}

// {mostly melee raiders} or {an EMP carrier} → no tank is placed (#1153).
func TestTankGating(t *testing.T) {
	melee := tankView()
	for i := range melee.Threats {
		melee.Threats[i].RangedEquipped = domain.Known(false)
	}
	emp := tankView()
	emp.Pawns = append(emp.Pawns, CombatPawnState{ID: "r1", Cell: domain.Known(domain.Cell{X: 9, Z: 5}), Weapon: "Gun_EmpLauncher"})
	for name, view := range map[string]CombatView{"melee": melee, "emp": emp} {
		_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
		if got := tankCellsOf(memory); len(got) != 0 {
			t.Errorf("%s: tanks %+v", name, got)
		}
	}
	if !tankThreat(tankView()) {
		t.Error("ranged raiders: want a tank")
	}
}
