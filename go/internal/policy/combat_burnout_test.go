package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// burnRooms is the base north of the hive: the line room (door south to
// the cave) and a back room one more door behind it.
var (
	lineRoom = CombatRoom{Interior: Rectangle{X: 0, Z: 20, Width: 20, Height: 6}, Doors: []domain.Cell{{X: 5, Z: 19}}}
	backRoom = CombatRoom{Interior: Rectangle{X: 0, Z: 27, Width: 5, Height: 4}, Doors: []domain.Cell{{X: 2, Z: 26}}}
)

// burnView is molotovHive with the given rooms and fuel, less the scarabs
// by the hive, so the three riflemen are not outmatched and never wait.
func burnView(tick domain.Tick, temp float64, fuel domain.Fact[int], rooms ...CombatRoom) CombatView {
	view := molotovHive(tick, temp)
	view.Threats = slices.DeleteFunc(view.Threats, func(t SquadThreatFacts) bool { return t.ID[0] == 'h' })
	view.Positional = slices.DeleteFunc(view.Positional, func(t DefensiveThreatFacts) bool { return t.ID[0] == 'h' })
	view.Rooms, view.BurnFuel = rooms, fuel
	return view
}

func molotovAtHive(orders []CombatOrder) bool {
	for _, o := range orders {
		if o.Pawn == "a" && o.Kind == OrderAttackGround {
			return o.Cell == hiveCell
		}
	}
	return false
}

// {a molotov carrier, the hive two doors from the back room} -> while the
// census shows too few stools the burn-out waits on its fuel and throws
// nothing; once four stand, a throws its molotov at the hive.
func TestBurnOutFuelsThenIgnites(t *testing.T) {
	orders, m := decideStop(t, burnView(100, 30, domain.Known(1), lineRoom, backRoom), StopEvent{}, CombatMemory{})
	if !m.Burn.Fueling() || m.Burn.Carrier != "a" || m.Burn.Hive != hiveCell || m.Burn.Room != backRoom.Interior {
		t.Fatalf("burn %+v", m.Burn)
	}
	for _, o := range orders {
		if o.Kind == OrderAttackGround {
			t.Fatalf("threw before the fuel stood: %+v", orders)
		}
	}
	orders, m = decideStop(t, burnView(200, 30, domain.Known(burnFuelStools), lineRoom, backRoom), StopEvent{}, m)
	if !molotovAtHive(orders) || m.Burn.Lit != 200 {
		t.Fatalf("no ignition: %+v %+v", orders, m.Burn)
	}
}

// {the burn-out lit} -> everyone else moves into the back room at once
// behind closed, forbidden doors; the carrier follows after its throw; the
// retreat holds while the hive is hot and releases once it cools.
func TestBurnOutRetreatsBehindDoors(t *testing.T) {
	_, m := decideStop(t, burnView(100, 30, domain.Known(burnFuelStools), lineRoom, backRoom), StopEvent{}, CombatMemory{})
	if m.Burn == nil || m.Burn.Lit == 0 || !m.Wait {
		t.Fatalf("not lit: %+v", m)
	}
	inBack := func(m CombatMemory, pawn domain.PawnID) bool {
		for _, r := range m.Roles {
			if r.Pawn == pawn {
				return r.Cell != nil && (CombatRoom{Interior: backRoom.Interior}).contains(*r.Cell) && r.Retreat
			}
		}
		return false
	}
	if !inBack(m, "b") || !inBack(m, "c") || inBack(m, "a") {
		t.Fatalf("retreat %+v", m.Roles)
	}
	forbidden := map[domain.Cell]bool{}
	for _, d := range m.WaitDoors {
		forbidden[d.Cell] = forbidden[d.Cell] || d.Mode == DoorForbid
	}
	if !forbidden[lineRoom.Doors[0]] || !forbidden[backRoom.Doors[0]] {
		t.Fatalf("doors %+v", m.WaitDoors)
	}
	lit := m.Burn.Lit
	_, m = decideStop(t, burnView(lit+burnThrowTicks, 180, domain.Unknown[int](), lineRoom, backRoom), StopEvent{}, m)
	if !inBack(m, "a") || !m.Wait {
		t.Fatalf("carrier did not follow: %+v", m.Roles)
	}
	_, m = decideStop(t, burnView(lit+burnFireTicks, 180, domain.Unknown[int](), lineRoom, backRoom), StopEvent{}, m)
	if m.Burn.Done || !m.Wait {
		t.Fatalf("left while hot: %+v", m.Burn)
	}
	_, m = decideStop(t, burnView(lit+burnFireTicks+100, 30, domain.Unknown[int](), lineRoom, backRoom), StopEvent{}, m)
	if !m.Burn.Done {
		t.Fatalf("fire out, still burning: %+v", m.Burn)
	}
	_, m = decideStop(t, burnView(lit+burnFireTicks+200, 30, domain.Unknown[int](), lineRoom, backRoom), StopEvent{}, m)
	if m.Wait {
		t.Fatalf("wait held after the fire: %+v", m)
	}
}

// {only the line room between the hive and the colonists: one door} -> no
// burn-out: the fuel is never asked for and the molotov keeps its #1073
// heat-stroke throw.
func TestNoIgniteWithFewDoors(t *testing.T) {
	orders, m := decideStop(t, burnView(100, 30, domain.Known(burnFuelStools), lineRoom), StopEvent{}, CombatMemory{})
	if m.Burn != nil {
		t.Fatalf("burn with one door: %+v", m.Burn)
	}
	if !molotovAtHive(orders) {
		t.Fatalf("heat-stroke throw lost: %+v", orders)
	}
}

// {a census around the hive with one stool, a wall and free floor} -> the
// fuel tier places three stools on the nearest free floor, never the hive.
func TestBurnFuelPlacesStools(t *testing.T) {
	census := map[domain.Cell]WaitDoorCell{hiveCell: {Edifice: "Hive", Walkable: true}}
	for x := hiveCell.X - 4; x <= hiveCell.X+4; x++ {
		for z := hiveCell.Z - 4; z <= hiveCell.Z+4; z++ {
			if c := (domain.Cell{X: x, Z: z}); c != hiveCell {
				census[c] = WaitDoorCell{Walkable: true}
			}
		}
	}
	census[domain.Cell{X: 9, Z: 13}] = WaitDoorCell{Edifice: BurnFuelDef, Walkable: true}
	census[domain.Cell{X: 8, Z: 12}] = WaitDoorCell{Edifice: "Wall"}
	got, err := BurnFuel(hiveCell, census)
	if err != nil || len(got) != burnFuelStools-1 {
		t.Fatalf("%v %+v", err, got)
	}
	for _, b := range got {
		if b.Cell() == hiveCell || census[b.Cell()].Edifice != "" || distance2(b.Cell(), hiveCell) > 2 {
			t.Fatalf("stool at %v", b.Cell())
		}
	}
}
