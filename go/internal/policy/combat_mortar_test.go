package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// mortarView is the camped siege with brawler m nearest our mortar at
// (9,30) and an enemy mortar at the camp, 50 cells south.
func mortarView() CombatView {
	view := sortieView()
	view.Mortars = []CombatMortar{{ID: "Thing_Turret_Mortar1", Cell: domain.Cell{X: 5, Z: 30}, MinRange: 29.9, MaxRange: 500}}
	view.Structures = []HostileStructure{
		{ID: "Thing_ShipPart2", Def: "DefoliatorShipPart", Cell: domain.Cell{X: 9, Z: -10}},
		{ID: "Thing_Turret_Mortar3", Def: "Turret_Mortar", Cell: domain.Cell{X: 9, Z: -20}, Mortar: true},
	}
	return view
}

func mortarOrders(orders []CombatOrder) []CombatOrder {
	var out []CombatOrder
	for _, o := range orders {
		if o.Kind == OrderManMortar {
			out = append(out, o)
		}
	}
	return out
}

// {our mortar, an enemy mortar and a ship part in range} -> the nearest
// pawn crews it against the enemy mortar (#931); once manning it, no
// order again.
func TestDecideCombatCounterBatteryCrewsTheMortar(t *testing.T) {
	view := mortarView()
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	want := CombatOrder{Pawn: "m", Kind: OrderManMortar, Cell: domain.Cell{X: 5, Z: 30}, Aim: domain.Cell{X: 9, Z: -20}, Shell: ShellEMP, Reason: ReasonCounterBattery}
	if got := mortarOrders(orders); len(got) != 1 || got[0] != want {
		t.Fatalf("%+v", got)
	}
	// The pawnless aim (#1202) leads its crew order.
	fire := CombatOrder{Kind: OrderMortarFire, Cell: want.Cell, Aim: want.Aim, Shell: ShellEMP, Reason: ReasonCounterBattery}
	if i := slices.Index(orders, fire); i < 0 || i+1 >= len(orders) || orders[i+1] != want {
		t.Fatalf("no mortar_fire before the crew: %+v", orders)
	}
	for i := range view.Pawns {
		if view.Pawns[i].ID == "m" {
			view.Pawns[i].Job, view.Pawns[i].Cell = "ManTurret", domain.Known(domain.Cell{X: 5, Z: 29})
		}
	}
	view.Tick += 60
	if orders, _ := decideStop(t, view, StopEvent{}, m); len(mortarOrders(orders)) != 0 {
		t.Fatalf("re-ordered the crew: %+v", orders)
	}
}

// {only the ship part in range, no siege} -> the mortar fires HE at the
// part (#930).
func TestDecideCombatMortarShellsTheShipPart(t *testing.T) {
	view := mortarView()
	view.Structures = view.Structures[:1]
	for i := range view.Positional {
		view.Positional[i].LordJobClass = domain.Known("LordJob_AssaultColony")
	}
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	if got := mortarOrders(orders); len(got) != 1 || got[0].Aim != (domain.Cell{X: 9, Z: -10}) || got[0].Shell != ShellHE {
		t.Fatalf("%+v", got)
	}
}

// {the enemy mortar inside the minimum range, no other structure} -> no crew.
func TestDecideCombatMortarHoldsInsideMinimumRange(t *testing.T) {
	view := mortarView()
	view.Structures = []HostileStructure{{ID: "Thing_Turret_Mortar3", Def: "Turret_Mortar", Cell: domain.Cell{X: 5, Z: 10}, Mortar: true}}
	for i := range view.Pawns {
		if view.Pawns[i].ID == "r1" || view.Pawns[i].ID == "r2" {
			view.Pawns[i].Cell = domain.Known(domain.Cell{X: 5, Z: 12})
		}
	}
	if orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{}); len(mortarOrders(orders)) != 0 {
		t.Fatalf("%+v", orders)
	}
}

// {a ship part the only threat, a rifleman and a brawler} -> the rifleman
// shoots it and the brawler is not sent to it in melee (#930).
func TestDecideCombatShipPartIsDestroyedFromRange(t *testing.T) {
	part := SquadThreatFacts{ID: "Thing_ShipPart2", Dead: domain.Known(false), Building: true, LinesOfFire: map[domain.PawnID]bool{"a": true}}
	view := withBrawlers(CombatView{
		Tick:       100,
		Defenders:  []SquadDefenderFacts{combatRifleman("a")},
		Threats:    []SquadThreatFacts{part},
		Structures: []HostileStructure{{ID: domain.PawnID(part.ID), Def: "DefoliatorShipPart", Cell: domain.Cell{X: 9, Z: 5}}},
		Pawns:      []CombatPawnState{{ID: "a", Cell: domain.Known(domain.Cell{X: 1, Z: 30}), Stance: StanceIdle}},
		Orderable:  []domain.PawnID{"a"},
	}, combatBrawler("m", 0.5))
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	a := attacks(orders)
	if a["a"] != domain.PawnID(part.ID) || a["m"] != "" {
		t.Fatalf("%+v", orders)
	}
}

// TestMortarCounterBattery (#1051): {two mortars, the camped siege of r1
// and r2, an enemy mortar} -> EMP on the enemy mortar, HE on the camp from
// the other; {no enemy mortar} -> HE on the camp, then incendiary from the
// second mortar; {HE refused no_shell} -> the loaded shell.
func TestMortarCounterBattery(t *testing.T) {
	two := func() CombatView {
		view := withBrawlers(mortarView(), combatBrawler("n", 0.5))
		view.Mortars = append(view.Mortars, CombatMortar{ID: "Thing_Turret_Mortar2", Cell: domain.Cell{X: 12, Z: 30}, MinRange: 29.9, MaxRange: 500, Loaded: ShellIncendiary})
		view.Structures = view.Structures[1:]
		view.Structures[0].Cell = domain.Cell{X: 30, Z: -20}
		return view
	}
	shells := func(orders []CombatOrder) map[domain.Cell][2]any {
		out := map[domain.Cell][2]any{}
		for _, o := range mortarOrders(orders) {
			out[o.Cell] = [2]any{o.Aim, o.Shell}
		}
		return out
	}
	m1, m2, camp := domain.Cell{X: 5, Z: 30}, domain.Cell{X: 12, Z: 30}, domain.Cell{X: 9, Z: -20}
	orders, _ := decideStop(t, two(), StopEvent{}, CombatMemory{})
	got := shells(orders)
	if got[m1] != [2]any{domain.Cell{X: 30, Z: -20}, ShellEMP} || got[m2] != [2]any{domain.Cell{X: 30, Z: -20}, ShellEMP} {
		t.Fatalf("enemy mortar: %+v", got)
	}
	view := two()
	view.Structures = nil
	orders, _ = decideStop(t, view, StopEvent{}, CombatMemory{})
	if got := shells(orders); got[m1] != [2]any{camp, ShellHE} || got[m2] != [2]any{camp, ShellIncendiary} {
		t.Fatalf("camp: %+v", got)
	}
	orders, _ = decideStop(t, view, StopEvent{}, CombatMemory{NoShells: []string{ShellHE}})
	if got := shells(orders); got[m1] != [2]any{camp, ""} || got[m2] != [2]any{camp, ShellIncendiary} {
		t.Fatalf("no HE: %+v", got)
	}
}

// TestMortarSiegeFire (#1208): {the siege camped, no structure} -> HE on
// the camp; {the siege still travelling in} -> no mortar order; {a
// colonist within 10 cells of the camp} -> no mortar order; {a camp of
// mechs} -> EMP.
func TestMortarSiegeFire(t *testing.T) {
	camp := domain.Cell{X: 9, Z: -20}
	view := func(toil string) CombatView {
		v := withBrawlers(siegeView(toil), combatBrawler("m", 0.5))
		v.Mortars = mortarView().Mortars
		return v
	}
	orders, _ := decideStop(t, view(siegeCampToil), StopEvent{}, CombatMemory{})
	if got := mortarOrders(orders); len(got) != 1 || got[0].Aim != camp || got[0].Shell != ShellHE {
		t.Fatalf("camped: %+v", got)
	}
	if orders, _ := decideStop(t, view(siegeTravel), StopEvent{}, CombatMemory{}); len(mortarOrders(orders)) != 0 {
		t.Fatalf("shelled a moving siege: %+v", mortarOrders(orders))
	}
	near := view(siegeCampToil)
	for i, p := range near.Pawns {
		if p.ID == "a" {
			near.Pawns[i].Cell = domain.Known(domain.Cell{X: 9, Z: -12})
		}
	}
	if orders, _ := decideStop(t, near, StopEvent{}, CombatMemory{}); len(mortarOrders(orders)) != 0 {
		t.Fatalf("shelled beside a colonist: %+v", mortarOrders(orders))
	}
	mechs := view(siegeCampToil)
	for i, p := range mechs.Pawns {
		if p.ID == "r1" || p.ID == "r2" {
			mechs.Pawns[i].Kind = "Mech_Lancer"
		}
	}
	orders, _ = decideStop(t, mechs, StopEvent{}, CombatMemory{})
	if got := mortarOrders(orders); len(got) != 1 || got[0].Shell != ShellEMP {
		t.Fatalf("mechs: %+v", got)
	}
}

// TestMortarHECentipede (#1051): {a centipede walking in 50 cells out, no
// structure, no siege} -> HE on it; {the centipede within 10 cells of a
// colonist} -> no mortar order.
func TestMortarHECentipede(t *testing.T) {
	view := holdView()
	view = withBrawlers(view, combatBrawler("m", 0.5))
	view.Mortars = []CombatMortar{{ID: "Thing_Turret_Mortar1", Cell: domain.Cell{X: 5, Z: 30}, MinRange: 29.9, MaxRange: 500}}
	view.Pawns = append(view.Pawns, CombatPawnState{ID: "r1", Cell: domain.Known(domain.Cell{X: 9, Z: -20}), Kind: "Mech_Centipede"})
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	if got := mortarOrders(orders); len(got) != 1 || got[0].Aim != (domain.Cell{X: 9, Z: -20}) || got[0].Shell != ShellHE {
		t.Fatalf("%+v", got)
	}
	view.Pawns[len(view.Pawns)-1].Cell = domain.Known(domain.Cell{X: 5, Z: -1})
	for i, p := range view.Pawns {
		if p.ID == "a" {
			view.Pawns[i].Cell = domain.Known(domain.Cell{X: 5, Z: 5})
		}
	}
	if orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{}); len(mortarOrders(orders)) != 0 {
		t.Fatalf("shelled a centipede at the line: %+v", mortarOrders(orders))
	}
}
