package policy

import (
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
		{ID: "Thing_Turret_Mortar3", Def: "Turret_Mortar", Cell: domain.Cell{X: 9, Z: -20}},
	}
	return view
}

func mortarOrders(orders []CombatOrder) []CombatOrder {
	var out []CombatOrder
	for _, o := range orders {
		if o.Kind == OrderMortar {
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
	want := CombatOrder{Pawn: "m", Kind: OrderMortar, Cell: domain.Cell{X: 5, Z: 30}, Aim: domain.Cell{X: 9, Z: -20}, Reason: ReasonCounterBattery}
	if got := mortarOrders(orders); len(got) != 1 || got[0] != want {
		t.Fatalf("%+v", got)
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

// {only the ship part in range} -> the mortar fires at the part (#930).
func TestDecideCombatMortarShellsTheShipPart(t *testing.T) {
	view := mortarView()
	view.Structures = view.Structures[:1]
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	if got := mortarOrders(orders); len(got) != 1 || got[0].Aim != (domain.Cell{X: 9, Z: -10}) {
		t.Fatalf("%+v", got)
	}
}

// {the enemy mortar inside the minimum range, no other structure} -> no crew.
func TestDecideCombatMortarHoldsInsideMinimumRange(t *testing.T) {
	view := mortarView()
	view.Structures = []HostileStructure{{ID: "Thing_Turret_Mortar3", Def: "Turret_Mortar", Cell: domain.Cell{X: 5, Z: 10}}}
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
