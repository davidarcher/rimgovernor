package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// kiteView is holdView with an inner line at z=24..26 behind the firing
// line, rifleman b fast (6.0 cells/s), a and c at a colonist's 4.6, and
// muffalo at speed facing the line from z=5.
func kiteView(speed float64) CombatView {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 24}, {X: 8, Z: 26}, {X: 10, Z: 25}}
	view.Layout = domain.Known(layout)
	for i := range view.Pawns {
		view.Pawns[i].MoveSpeed = 4.6
		if view.Pawns[i].ID == "b" {
			view.Pawns[i].MoveSpeed, view.Pawns[i].WeaponRange = 6.0, 31
		}
	}
	return withAnimals(view, animal("m1", "Muffalo", domain.Cell{X: 9, Z: 5}, speed), animal("m2", "Muffalo", domain.Cell{X: 10, Z: 4}, speed))
}

func role(m CombatMemory, id domain.PawnID) CombatRole {
	for _, r := range m.Roles {
		if r.Pawn == id {
			return r
		}
	}
	return CombatRole{}
}

// {slow pack, a defender at >=1.2x the animals' speed, not targeted} ->
// that defender baits: an attack from where it stands.
func TestDecideCombatManhunterKiterBaits(t *testing.T) {
	orders, m := decideStop(t, kiteView(3.5), StopEvent{}, CombatMemory{})
	if m.Kiter != "b" || m.Leading {
		t.Fatalf("%+v", m)
	}
	if r := role(m, "b"); r.Duty != DutyKiter || r.Cell != nil || r.Target == "" {
		t.Fatalf("%+v", r)
	}
	for _, o := range orders {
		if o.Pawn == "b" && o.Kind != OrderAttack {
			t.Fatalf("%+v", orders)
		}
	}
}

// {an animal targets the kiter} -> it retreats to the rearmost inner-line
// cell, past the aim guard.
func TestDecideCombatManhunterKiterLeadsPastLine(t *testing.T) {
	_, m := decideStop(t, kiteView(3.5), StopEvent{}, CombatMemory{})
	view := kiteView(3.5)
	view.Tick = 160
	for i := range view.Pawns {
		if view.Pawns[i].ID == "m1" {
			view.Pawns[i].Target = "b"
		}
		if view.Pawns[i].ID == "b" {
			view.Pawns[i].Stance = StanceWarmup
		}
	}
	orders, m := decideStop(t, view, StopEvent{}, m)
	want := domain.Cell{X: 8, Z: 26}
	if r := role(m, "b"); !m.Leading || r.Cell == nil || *r.Cell != want {
		t.Fatalf("%+v %+v", m, r)
	}
	found := false
	for _, o := range orders {
		found = found || o.Pawn == "b" && o.Kind == OrderMove && o.Cell == want && o.Reason == ReasonRetreat
	}
	if !found {
		t.Fatalf("%+v", orders)
	}
}

// {a fast animal, or no defender fast enough} -> no kiter.
func TestDecideCombatManhunterNoKiterForFastAnimals(t *testing.T) {
	for _, c := range []struct{ animal, b float64 }{{6.8, 6.0}, {4.5, 5.0}} {
		view := kiteView(c.animal)
		for i := range view.Pawns {
			if view.Pawns[i].ID == "b" {
				view.Pawns[i].MoveSpeed = c.b
			}
		}
		if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Kiter != "" {
			t.Fatalf("%+v: %+v", c, m)
		}
	}
}

// fastRifleman sets rifleman b to 6.0 cells/s and the others to 4.6.
func fastRifleman(view CombatView) CombatView {
	for i := range view.Pawns {
		switch view.Pawns[i].ID {
		case "a", "c":
			view.Pawns[i].MoveSpeed = 4.6
		case "b":
			view.Pawns[i].MoveSpeed, view.Pawns[i].WeaponRange = 6.0, 31
		}
	}
	return view
}

// centipedeView is holdView with the kite test's inner line, rifleman b
// fast, and two centipedes at 1.9 cells/s walking in from the south.
func centipedeView(extra ...combatMech) CombatView {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 24}, {X: 8, Z: 26}, {X: 10, Z: 25}}
	view.Layout = domain.Known(layout)
	mechs := append([]combatMech{
		{id: "c1", kind: "Mech_CentipedeGunner", cell: domain.Cell{X: 9, Z: 5}, speed: 1.9},
		{id: "c2", kind: "Mech_CentipedeBlaster", cell: domain.Cell{X: 10, Z: 4}, speed: 1.9},
	}, extra...)
	return fastRifleman(withMechs(view, mechs...))
}

func targeting(view CombatView, hostile, pawn domain.PawnID) CombatView {
	for i := range view.Pawns {
		if view.Pawns[i].ID == hostile {
			view.Pawns[i].Target = pawn
		}
	}
	return view
}

// {centipedes 1.9, rifleman 6, hold with an inner line} -> the kiter
// attacks the nearest centipede, then leads to the rearmost inner-line
// cell once targeted.
func TestDecideCombatMechKitesSlowCentipedes(t *testing.T) {
	orders, m := decideStop(t, centipedeView(), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticHold || m.Kiter != "b" || m.Leading {
		t.Fatalf("%+v", m)
	}
	if a := attacks(orders); a["b"] != "c1" {
		t.Fatalf("%+v", orders)
	}
	view := targeting(centipedeView(), "c1", "b")
	view.Tick = 160
	orders, m = decideStop(t, view, StopEvent{}, m)
	want := domain.Cell{X: 8, Z: 26}
	if r := role(m, "b"); !m.Leading || r.Cell == nil || *r.Cell != want {
		t.Fatalf("%+v %+v", m, r)
	}
	if got := moves(orders); got["b"] != want {
		t.Fatalf("%+v", orders)
	}
}

// {a scyther with the centipedes} -> no kiter.
func TestDecideCombatMechNoKiteWithScythers(t *testing.T) {
	view := centipedeView(combatMech{id: "s1", kind: "Mech_Scyther", cell: domain.Cell{X: 11, Z: 5}, speed: 4.7})
	if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Tactic != TacticHold || m.Kiter != "" {
		t.Fatalf("%+v", m)
	}
}

// {mech breachers, sapper tactic} -> the kiter baits, then leads to the
// breach's first gunner post inside the room.
func TestDecideCombatMechKitesBreachersInside(t *testing.T) {
	breachers := func() CombatView {
		view := withBrawlers(holdView(), combatBrawler("m", 0.5))
		view.Rooms = []CombatRoom{sapperRoom}
		view = withMechs(view,
			combatMech{id: "t1", kind: "Mech_Termite", cell: domain.Cell{X: 9, Z: 5}, speed: 2.1},
			combatMech{id: "t2", kind: "Mech_Termite", cell: domain.Cell{X: 10, Z: 4}, speed: 2.1})
		for i := range view.Pawns {
			view.Pawns[i].Sapper = view.Pawns[i].Kind == "Mech_Termite"
		}
		return fastRifleman(view)
	}
	_, m := decideStop(t, breachers(), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticSapper || m.Kiter != "b" || m.Leading {
		t.Fatalf("%+v", m)
	}
	view := targeting(breachers(), "t1", "b")
	view.Tick = 160
	orders, m := decideStop(t, view, StopEvent{}, m)
	want := domain.Cell{X: 10, Z: 22}
	if r := role(m, "b"); !m.Leading || r.Cell == nil || *r.Cell != want || !sapperRoom.contains(want) {
		t.Fatalf("%+v %+v", m, r)
	}
	if got := moves(orders); got["b"] != want {
		t.Fatalf("%+v", orders)
	}
}

// TestKiterEligibility: a kiter needs 1.2x the fastest chaser (1.4x
// against a fast animal), a long-range gun and light armor (#1061).
func TestKiterEligibility(t *testing.T) {
	rifle := CombatRole{Pawn: "b", Ranged: true}
	for _, c := range []struct {
		name  string
		role  CombatRole
		speed float64
		rng   float64
		armor domain.Fact[float64]
		want  bool
	}{
		{"fast rifle", rifle, 6.0, 31, domain.Unknown[float64](), true},
		{"too slow", rifle, 4.7, 31, domain.Unknown[float64](), false},
		{"short gun", rifle, 6.0, 25.9, domain.Unknown[float64](), false},
		{"heavy armor", rifle, 6.0, 31, domain.Known(1.06), false},
		{"light armor", rifle, 6.0, 31, domain.Known(0.4), true},
		{"brawler", CombatRole{Pawn: "b"}, 6.0, 31, domain.Unknown[float64](), false},
	} {
		s := CombatPawnState{ID: "b", MoveSpeed: c.speed, WeaponRange: c.rng}
		if got := kiterEligible(c.role, s, c.armor, 1.2*4.0); got != c.want {
			t.Errorf("%s: got %v", c.name, got)
		}
	}
	// A fast animal (a colonist's speed or more) needs 1.4x: 5.0 * 1.4 = 7.
	for _, c := range []struct {
		b    float64
		want domain.PawnID
	}{{6.9, ""}, {7.1, "b"}} {
		view := kiteView(5.0)
		for i := range view.Pawns {
			if view.Pawns[i].ID == "b" {
				view.Pawns[i].MoveSpeed = c.b
			}
		}
		if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Kiter != c.want {
			t.Errorf("b at %.1f vs a 5.0 pack: kiter %q", c.b, m.Kiter)
		}
	}
	// A heavily armored fast rifleman is not the kiter.
	view := kiteView(3.5)
	for i := range view.Defenders {
		if view.Defenders[i].ID == "b" {
			view.Defenders[i].Armor = domain.Known(1.2)
		}
	}
	if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Kiter != "" {
		t.Fatalf("%+v", m)
	}
}
