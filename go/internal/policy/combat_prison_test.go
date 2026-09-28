package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// prisonView is a prison break: escapees p1 (healthy) and p2 (injured)
// at z=10, wardens standing back at z=20. weapons are the defenders'
// equipped primaries by id ("" unarmed).
func prisonView(weapons map[domain.PawnID]string) CombatView {
	view := CombatView{Tick: 50}
	for _, e := range []struct {
		id     PawnID
		x      int32
		health float64
	}{{"p1", 5, 1}, {"p2", 9, 0.6}} {
		s, d := combatRaider(e.id, domain.Cell{X: e.x, Z: 10})
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(e.id), Cell: domain.Known(domain.Cell{X: e.x, Z: 10}), Prisoner: true, Health: domain.Known(e.health)})
	}
	x := int32(0)
	for _, id := range []domain.PawnID{"a", "b", "c", "d", "e", "f"} {
		w, ok := weapons[id]
		if !ok {
			continue
		}
		d := squadDefender(id, rangedDef(w))
		d.Armed, d.MeleeEquipped, d.Warden = domain.Known(w != ""), domain.Known(w != "" && !rangedDef(w)), true
		view.Defenders = append(view.Defenders, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(domain.Cell{X: x, Z: 20}), Stance: StanceIdle, Weapon: w, FireMode: FireAtWill})
		view.Orderable = append(view.Orderable, id)
		x++
	}
	return view
}

// One brawler (a blunt weapon first) per injured escapee, two unarmed
// wardens per healthy one; a sniper stays out.
func TestPrisonBreakBlock(t *testing.T) {
	view := prisonView(map[domain.PawnID]string{"a": "", "b": "MeleeWeapon_Mace", "c": "", "d": "MeleeWeapon_LongSword", "e": "Gun_SniperRifle"})
	orders, _, m := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticPrisonBreak {
		t.Fatalf("tactic %q", m.Tactic)
	}
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderAttack, Target: "p1", Reason: ReasonFormation},
		{Pawn: "b", Kind: OrderAttack, Target: "p2", Reason: ReasonFormation},
		{Pawn: "c", Kind: OrderAttack, Target: "p1", Reason: ReasonFormation},
	}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("orders %+v", orders)
	}
	// Both escapees down: the next stop re-forms out of the prison tactic.
	for i := range view.Pawns {
		if view.Pawns[i].Prisoner {
			view.Pawns[i].Downed = true
		}
	}
	if _, _, again := DecideCombat(view, GeometryReply{}, StopEvent{}, m); again.Tactic == TacticPrisonBreak {
		t.Fatalf("still %q with every escapee down", again.Tactic)
	}
}

// A ranged warden holds fire while no escapee is blocked, then fires on
// the blocked one; a lethal gun never gets a role.
func TestWardensHoldFireUntilBlocked(t *testing.T) {
	view := prisonView(map[domain.PawnID]string{"a": "MeleeWeapon_Club", "f": "Gun_Revolver", "e": "Gun_SniperRifle"})
	orders, _, m := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderAttack, Target: "p2", Reason: ReasonFormation},
		{Pawn: "f", Kind: OrderFireMode, FireMode: HoldFire, Reason: ReasonPrisonFire},
	}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("unblocked: %+v", orders)
	}
	// The club reaches p2 and fights it: f fires at will on p2.
	for i := range view.Pawns {
		switch view.Pawns[i].ID {
		case "a":
			view.Pawns[i].Cell, view.Pawns[i].Target, view.Pawns[i].Stance = domain.Known(domain.Cell{X: 9, Z: 11}), "p2", StanceMelee
		case "f":
			view.Pawns[i].FireMode = HoldFire
		}
	}
	orders, _, _ = DecideCombat(view, GeometryReply{}, StopEvent{}, m)
	want = []CombatOrder{
		{Pawn: "f", Kind: OrderFireMode, FireMode: FireAtWill, Reason: ReasonPrisonFire},
		{Pawn: "f", Kind: OrderAttack, Target: "p2", Reason: ReasonFormation},
	}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("blocked: %+v", orders)
	}
}
