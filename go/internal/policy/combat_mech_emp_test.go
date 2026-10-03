package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// mechChokeView is scytherView (lab-mech's scythers at a choke) after the
// hold formed: blockers d, e, f outside the choke, riflemen on the line
// with a holding weapon, scyther r1 in the choke fighting e and r2 still
// out in the corridor; the layout records an inner line behind the
// firing line.
func mechChokeView(t *testing.T, weapon string) (CombatView, CombatMemory) {
	t.Helper()
	view := scytherView()
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 25}, {X: 8, Z: 25}, {X: 10, Z: 25}}
	view.Layout = domain.Known(layout)
	_, memory := decideAny(t, view, StopEvent{}, CombatMemory{})
	view.Tick = 260
	at := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}, "d": {X: 10, Z: 18}, "e": {X: 9, Z: 18}, "f": {X: 8, Z: 18}}
	r1, r2 := domain.Cell{X: 9, Z: 17}, domain.Cell{X: 10, Z: 9}
	for i := range view.Pawns {
		p := &view.Pawns[i]
		if c, ok := at[p.ID]; ok {
			p.Cell, p.FireMode = domain.Known(c), FireAtWill
		}
		switch p.ID {
		case "a":
			p.Weapon, p.WeaponFacts, p.WeaponRange = weapon, coreWeapons[weapon], 0
		case "e":
			p.Target, p.Stance = "r1", StanceMelee
		case "r1":
			p.Cell, p.Target, p.Stance = domain.Known(r1), "e", StanceMelee
		case "r2":
			p.Cell = domain.Known(r2)
		}
	}
	view.Positional[0].Position, view.Positional[0].NearestColonistDistance = domain.Known(r1), domain.Known(1.0)
	view.Positional[1].Position, view.Positional[1].NearestColonistDistance = domain.Known(r2), domain.Known(9.0)
	return view, memory
}

func setPawn(view *CombatView, id domain.PawnID, f func(*CombatPawnState)) {
	for i := range view.Pawns {
		if view.Pawns[i].ID == id {
			f(&view.Pawns[i])
		}
	}
}

// {EMP carrier, scyther r1 in the choke beside the blockers, r2 out in the
// corridor} -> one attack_ground whose blast reaches r1 and stays clear of
// the blockers; r2 alone (r1 dead) draws none, nor does r1 once stunned
// or adapted.
func TestMechEMPOnlyChokeScythers(t *testing.T) {
	view, memory := mechChokeView(t, "Weapon_GrenadeEMP")
	orders, _ := decideAny(t, view, StopEvent{Kind: StopMeleeContact, Pawn: "r1", Target: "e"}, memory)
	ground := groundOrders(orders)
	if len(ground) != 1 || ground[0].Pawn != "a" || dist(ground[0].Cell, domain.Cell{X: 9, Z: 17}) > coreWeapons["Weapon_GrenadeEMP"].Blast {
		t.Fatalf("want one EMP onto r1: %+v", orders)
	}
	for _, b := range []domain.Cell{{X: 10, Z: 18}, {X: 9, Z: 18}, {X: 8, Z: 18}} {
		if chebyshev(ground[0].Cell, b) <= grenadeScatterClear {
			t.Fatalf("EMP at %v scatters onto blocker %v", ground[0].Cell, b)
		}
	}

	stunned, _ := mechChokeView(t, "Weapon_GrenadeEMP")
	setPawn(&stunned, "r1", func(p *CombatPawnState) { p.StunTicks = 1500 })
	if orders, _ := decideAny(t, stunned, StopEvent{}, memory); len(groundOrders(orders)) != 0 {
		t.Fatalf("a stunned scyther drew another EMP: %+v", orders)
	}

	adapted, _ := mechChokeView(t, "Weapon_GrenadeEMP")
	m := memory
	m.EMPAdapted = []EMPAdaptation{{Pawn: "r1", Until: 5000}}
	if orders, _ := decideAny(t, adapted, StopEvent{}, m); len(groundOrders(orders)) != 0 {
		t.Fatalf("an adapted scyther drew an EMP: %+v", orders)
	}

	far, _ := mechChokeView(t, "Weapon_GrenadeEMP")
	setPawn(&far, "r1", func(p *CombatPawnState) { p.Dead = true })
	if orders, _ := decideAny(t, far, StopEvent{}, memory); len(groundOrders(orders)) != 0 {
		t.Fatalf("a scyther outside the choke drew an EMP: %+v", orders)
	}
}

// {r1 stunned in the choke} -> the blockers keep fighting while the stun
// has long to run and r1 is recorded adapted; once it has
// empDisengageTicks left, every blocker by it moves to the inner line.
func TestMechDisengageBeforeReactivate(t *testing.T) {
	view, memory := mechChokeView(t, "")
	setPawn(&view, "r1", func(p *CombatPawnState) { p.StunTicks, p.Stance = 1500, StanceIdle })
	orders, memory := decideAny(t, view, StopEvent{}, memory)
	if want := []EMPAdaptation{{Pawn: "r1", Until: 260 + 1500 + empAdaptAfterStun}}; !reflect.DeepEqual(memory.EMPAdapted, want) {
		t.Fatalf("adaptation %+v, want %+v", memory.EMPAdapted, want)
	}
	for _, o := range orders {
		if o.Reason == ReasonRetreat {
			t.Fatalf("pulled back with the stun far from over: %+v", orders)
		}
	}

	view.Tick = 1680
	setPawn(&view, "r1", func(p *CombatPawnState) { p.StunTicks = 90 })
	orders, memory = decideAny(t, view, StopEvent{}, memory)
	back := map[domain.PawnID]domain.Cell{}
	for _, o := range orders {
		if o.Reason == ReasonRetreat && o.Kind == OrderMove {
			back[o.Pawn] = o.Cell
		}
	}
	want := map[domain.PawnID]domain.Cell{"d": {X: 9, Z: 25}, "e": {X: 8, Z: 25}, "f": {X: 10, Z: 25}}
	if !reflect.DeepEqual(back, want) {
		t.Fatalf("retreats %v, want %v\n%+v", back, want, orders)
	}
	if len(memory.EMPAdapted) != 1 {
		t.Fatalf("adaptation lost: %+v", memory.EMPAdapted)
	}
}

// {a Molotov carrier, only mechs in reach} -> no throw; with a raider in
// the mix the Molotov counts the raider only.
func TestNoIncendiaryOnMechs(t *testing.T) {
	carrier := withWeapon(CombatPawnState{ID: "a", Cell: domain.Known(domain.Cell{X: 9, Z: 23})}, "Weapon_GrenadeMolotov")
	mechs := []CombatPawnState{
		{ID: "m1", Kind: "Mech_Scyther", Cell: domain.Known(domain.Cell{X: 9, Z: 14})},
		{ID: "m2", Kind: "Mech_Lancer", Cell: domain.Known(domain.Cell{X: 10, Z: 14})},
	}
	if c, ok := GrenadeTarget(carrier, mechs, nil); ok {
		t.Fatalf("Molotov at mechs: %v", c)
	}
	mixed := append(mechs, CombatPawnState{ID: "h1", Kind: "Pirate", Cell: domain.Known(domain.Cell{X: 5, Z: 16})})
	if c, ok := GrenadeTarget(carrier, mixed, nil); !ok || c != (domain.Cell{X: 5, Z: 16}) {
		t.Fatalf("got %v %v, want the raider at 5,16", c, ok)
	}
	view, memory := mechChokeView(t, "Weapon_GrenadeMolotov")
	if orders, _ := decideAny(t, view, StopEvent{}, memory); len(groundOrders(orders)) != 0 {
		t.Fatalf("Molotov thrown at scythers: %+v", orders)
	}
}
