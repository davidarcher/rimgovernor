package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// withPets adds colony animals, idle, at cells.
func withPets(view CombatView, cells map[domain.PawnID]domain.Cell) CombatView {
	for id, c := range cells {
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Animal: true, Cell: domain.Known(c), Stance: StanceIdle})
	}
	return view
}

// animalOrders are the orders naming an animal order kind.
func animalOrders(orders []CombatOrder) []CombatOrder {
	var out []CombatOrder
	for _, o := range orders {
		if AnimalOrderKind(o.Kind) {
			out = append(out, o)
		}
	}
	return out
}

// {melee raiders r1 (9,5), r2 (10,4); dog at (5,28)} -> dog released at
// the nearer r1; {dog on r1} -> nothing; {refused untrained} -> never
// released again.
func TestAnimalReleaseCharge(t *testing.T) {
	view := withPets(holdView(), map[domain.PawnID]domain.Cell{"dog": {X: 5, Z: 28}})
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	want := []CombatOrder{{Pawn: "dog", Kind: OrderRelease, Target: "r1", Reason: ReasonAnimal}}
	if got := animalOrders(orders); !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", got)
	}
	view.Tick++
	for i := range view.Pawns {
		if view.Pawns[i].ID == "dog" {
			view.Pawns[i].Target, view.Pawns[i].Stance = "r1", StanceMelee
		}
	}
	if orders, _ = decideStop(t, view, StopEvent{}, m); len(animalOrders(orders)) != 0 {
		t.Fatalf("repeat: %+v", orders)
	}
	refused := m.RefuseAnimal(want[0], refusalUntrained)
	view = withPets(holdView(), map[domain.PawnID]domain.Cell{"dog": {X: 5, Z: 28}})
	if orders, _ = decideStop(t, view, StopEvent{}, refused); len(animalOrders(orders)) != 0 || !reflect.DeepEqual(refused.Untrained, []domain.PawnID{"dog"}) {
		t.Fatalf("untrained: %+v", orders)
	}
	// Ranged raiders are no release target.
	ranged := holdView()
	for i := range ranged.Threats {
		ranged.Threats[i].RangedEquipped = domain.Known(true)
	}
	if orders, _ = decideStop(t, withPets(ranged, map[domain.PawnID]domain.Cell{"dog": {X: 5, Z: 28}}), StopEvent{}, CombatMemory{}); len(animalOrders(orders)) != 0 {
		t.Fatalf("ranged: %+v", orders)
	}
}

// {grenadier r1 at (9,5), nearest colonist c at (3,30); dog} -> dog zoned
// beside r1 on our side at (8,6), not released.
func TestAnimalDecoy(t *testing.T) {
	view := holdView()
	armRaider(&view, "r1", "Weapon_GrenadeFrag", 12.9)
	view = withPets(view, map[domain.PawnID]domain.Cell{"dog": {X: 5, Z: 28}})
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	want := []CombatOrder{{Pawn: "dog", Kind: OrderAnimalArea, Cell: domain.Cell{X: 8, Z: 6}, Reason: ReasonAnimal}}
	if got := animalOrders(orders); !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", got)
	}
	// The zone is sent once.
	view.Tick++
	if orders, _ = decideStop(t, view, StopEvent{}, m); len(animalOrders(orders)) != 0 {
		t.Fatalf("repeat: %+v", orders)
	}
}

// {manhunter wolves, choke (9,17); dog, cat} -> both zoned onto the
// choke; {pikemen with a choke} -> the same; {no choke} -> nothing.
func TestAnimalChokeBlock(t *testing.T) {
	pets := map[domain.PawnID]domain.Cell{"cat": {X: 6, Z: 28}, "dog": {X: 5, Z: 28}}
	choke := domain.Cell{X: 9, Z: 17}
	want := []CombatOrder{
		{Pawn: "cat", Kind: OrderAnimalArea, Cell: choke, Reason: ReasonAnimal},
		{Pawn: "dog", Kind: OrderAnimalArea, Cell: choke, Reason: ReasonAnimal},
	}
	wolves := withAnimals(chokeView(), animal("w0", "Wolf_Timber", domain.Cell{X: 9, Z: 5}, 6.8))
	orders, _ := decideAny(t, withPets(wolves, pets), StopEvent{}, CombatMemory{})
	if got := animalOrders(orders); !reflect.DeepEqual(got, want) {
		t.Fatalf("manhunters: %+v", got)
	}
	pikemen := pikemenView()
	layout, _ := pikemen.Layout.Value()
	layout.Choke = domain.Known(choke)
	pikemen.Layout = domain.Known(layout)
	orders, _ = decideAny(t, withPets(pikemen, pets), StopEvent{}, CombatMemory{})
	if got := animalOrders(orders); !reflect.DeepEqual(got, want) {
		t.Fatalf("pikemen: %+v", got)
	}
	orders, _ = decideStop(t, withPets(pikemenView(), pets), StopEvent{}, CombatMemory{})
	if got := animalOrders(orders); len(got) != 0 {
		t.Fatalf("no choke: %+v", got)
	}
}

// {waiting out a pack; dog on door (15,19), cat at (12,22)} -> dog zoned
// to the room's floor (15,20), cat to its own cell; {the wait ends} ->
// both cleared; the fight's close clears what is still zoned.
func TestAnimalsOffDoors(t *testing.T) {
	pets := map[domain.PawnID]domain.Cell{"cat": {X: 12, Z: 22}, "dog": {X: 15, Z: 19}}
	orders, m := decideStop(t, withPets(waitView(5), pets), StopEvent{}, CombatMemory{})
	if !m.Wait {
		t.Fatal("not waiting")
	}
	want := []CombatOrder{
		{Pawn: "cat", Kind: OrderAnimalArea, Cell: domain.Cell{X: 12, Z: 22}, Reason: ReasonAnimal},
		{Pawn: "dog", Kind: OrderAnimalArea, Cell: domain.Cell{X: 15, Z: 20}, Reason: ReasonAnimal},
	}
	if got := animalOrders(orders); !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", got)
	}
	clears := []CombatOrder{
		{Pawn: "cat", Kind: OrderAnimalClear, Reason: ReasonAnimal},
		{Pawn: "dog", Kind: OrderAnimalClear, Reason: ReasonAnimal},
	}
	if got := AnimalClears(m); !reflect.DeepEqual(got, clears) {
		t.Fatalf("close: %+v", got)
	}
	view := withPets(waitView(1), pets)
	view.Tick++
	orders, next := decideStop(t, view, StopEvent{}, m)
	if next.Wait {
		t.Fatal("still waiting")
	}
	if got := animalOrders(orders); !reflect.DeepEqual(got, clears) {
		t.Fatalf("wait over: %+v", got)
	}
	if len(next.Animals) != 0 || len(AnimalClears(next)) != 0 {
		t.Fatalf("%+v", next.Animals)
	}
}
