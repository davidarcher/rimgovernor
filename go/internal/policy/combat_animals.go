package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Animal tactics order the colony's animals with the animal
// orders, which need no draft claim. One use per stop, first match wins:
//  - waiting a manhunter pack out: every animal is zoned to its own
//     cell, one standing on a door to the nearest floor cell of that door's
//     room, so no animal opens a door;
//  - an explosives raid: each animal is zoned beside an explosive
//     hostile, on our side, so its shots and blasts hit its own raiders;
//  - a manhunter pack without exploders, or pikemen, with a known
//     choke: every animal is zoned onto the choke cell as a body blocker;
//   - melee raiders: every animal not known untrained is released at the
//     nearest one.
//
// An animal the fight zoned and no longer wants zoned is cleared, which
// restores its prior restriction; the fight's close clears the rest
// (AnimalClears).

// Animal order kinds.
const (
	OrderRelease     CombatOrderKind = "release"
	OrderAnimalArea  CombatOrderKind = "animal_area"
	OrderAnimalClear CombatOrderKind = "animal_clear"
)

// ReasonAnimal is an animal tactic's order.
const ReasonAnimal CombatOrderReason = "animal"

// AnimalOrder is the last release or zone an animal was given.
type AnimalOrder struct {
	Pawn   domain.PawnID
	Kind   CombatOrderKind
	Cell   domain.Cell   `json:",omitzero"`
	Target domain.PawnID `json:",omitempty"`
}

func (a AnimalOrder) order() CombatOrder {
	return CombatOrder{Pawn: a.Pawn, Kind: a.Kind, Cell: a.Cell, Target: a.Target, Reason: ReasonAnimal}
}

// colonyAnimals are the live colony animals with a known cell, by id.
func colonyAnimals(view CombatView) []CombatPawnState {
	var out []CombatPawnState
	for _, p := range view.Pawns {
		if _, ok := p.Cell.Value(); ok && p.Animal && !p.Dead && !p.Downed {
			out = append(out, p)
		}
	}
	return out
}

// animalWants is what each colony animal should be doing this stop.
func animalWants(view CombatView, m CombatMemory) map[domain.PawnID]AnimalOrder {
	animals := colonyAnimals(view)
	want := map[domain.PawnID]AnimalOrder{}
	zone := func(p CombatPawnState, c domain.Cell) {
		want[p.ID] = AnimalOrder{Pawn: p.ID, Kind: OrderAnimalArea, Cell: c}
	}
	switch {
	case len(animals) == 0:
	case m.Wait && m.Tactic == TacticManhunter:
		for _, p := range animals {
			at, _ := p.Cell.Value()
			zone(p, offDoor(view, at))
		}
	case len(explosiveHostiles(view)) > 0:
		cells := decoyCells(view, explosiveHostiles(view))
		if len(cells) == 0 {
			break
		}
		for i, p := range animals {
			zone(p, cells[i%len(cells)])
		}
	case animalChoke(view) != nil:
		for _, p := range animals {
			zone(p, *animalChoke(view))
		}
	default:
		ids, cells := meleeRaiders(view)
		if len(ids) == 0 {
			break
		}
		for _, p := range animals {
			if slices.Contains(m.Untrained, p.ID) {
				continue
			}
			at, _ := p.Cell.Value()
			want[p.ID] = AnimalOrder{Pawn: p.ID, Kind: OrderRelease, Target: ids[nearestIndex(at, cells)]}
		}
	}
	return want
}

// animalStep turns the wants into changed orders and records them in m.
// An animal switching from a zone to a release is cleared first and
// released on a later stop, so a batch names an animal once.
func animalStep(view CombatView, m *CombatMemory) []CombatOrder {
	want := animalWants(view, *m)
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	var out []CombatOrder
	var kept []AnimalOrder
	for _, had := range m.Animals {
		s, present := state[had.Pawn]
		w, wanted := want[had.Pawn]
		switch {
		case !present || s.Dead:
			// Gone or dead: nothing to restore.
		case had.Kind == OrderAnimalArea && (!wanted || w.Kind != OrderAnimalArea):
			out = append(out, CombatOrder{Pawn: had.Pawn, Kind: OrderAnimalClear, Reason: ReasonAnimal})
			delete(want, had.Pawn)
		case wanted && w == had && (w.Kind == OrderAnimalArea || s.Target == w.Target || s.Stance != StanceIdle):
			// Already doing it.
			kept = append(kept, had)
			delete(want, had.Pawn)
		case had.Kind == OrderAnimalArea:
			// Rezoned below; the native area moves to the new cell.
		}
	}
	ids := make([]domain.PawnID, 0, len(want))
	for id := range want {
		ids = append(ids, id)
	}
	slices.Sort(ids)
	for _, id := range ids {
		out = append(out, want[id].order())
		kept = append(kept, want[id])
	}
	slices.SortFunc(kept, func(a, b AnimalOrder) int { return strings.Compare(string(a.Pawn), string(b.Pawn)) })
	m.Animals = kept
	return out
}

// AnimalClears are the clears for every animal the fight still has zoned,
// sent when the fight closes.
func AnimalClears(m CombatMemory) []CombatOrder {
	var out []CombatOrder
	for _, a := range m.Animals {
		if a.Kind == OrderAnimalArea {
			out = append(out, CombatOrder{Pawn: a.Pawn, Kind: OrderAnimalClear, Reason: ReasonAnimal})
		}
	}
	return out
}

// RefuseAnimal is Forget for a refused animal order: an animal refused a
// release as untrained is never released again this fight; any other
// refusal is given again on the next stop.
func (m CombatMemory) RefuseAnimal(order CombatOrder, refusal string) CombatMemory {
	m = m.Forget(order.Pawn)
	m.Animals = slices.DeleteFunc(m.Animals, func(a AnimalOrder) bool { return a.Pawn == order.Pawn })
	if order.Kind == OrderRelease && refusal == refusalUntrained && !slices.Contains(m.Untrained, order.Pawn) {
		m.Untrained = append(m.Untrained, order.Pawn)
		slices.Sort(m.Untrained)
	}
	return m
}

// refusalUntrained is bridge.CombatRefusalUntrained.
const refusalUntrained = "untrained"

// AnimalOrderKind reports an animal order.
func AnimalOrderKind(k CombatOrderKind) bool {
	return k == OrderRelease || k == OrderAnimalArea || k == OrderAnimalClear
}

// offDoor is at, or when at is a room's door the nearest floor cell of
// that room.
func offDoor(view CombatView, at domain.Cell) domain.Cell {
	for _, r := range view.Rooms {
		if !slices.Contains(r.Doors, at) {
			continue
		}
		in := r.Interior
		best, bestD := at, int64(-1)
		for x := in.X; x < in.X+in.Width; x++ {
			for z := in.Z; z < in.Z+in.Height; z++ {
				c := domain.Cell{X: x, Z: z}
				if d := distance2(at, c); !slices.Contains(r.Doors, c) && (bestD < 0 || d < bestD) {
					best, bestD = c, d
				}
			}
		}
		return best
	}
	return at
}

// decoyCells are the cells one step from each explosive hostile toward
// the nearest live colonist, in threat order.
func decoyCells(view CombatView, explosive []CombatPawnState) []domain.Cell {
	var ours []domain.Cell
	for _, d := range view.Defenders {
		if positive(d.Dead) || positive(d.Downed) {
			continue
		}
		for _, p := range view.Pawns {
			if c, ok := p.Cell.Value(); ok && p.ID == d.ID && !p.Dead && !p.Downed {
				ours = append(ours, c)
			}
		}
	}
	var out []domain.Cell
	for _, h := range explosive {
		at, ok := h.Cell.Value()
		if !ok || len(ours) == 0 {
			continue
		}
		to := ours[nearestIndex(at, ours)]
		c := domain.Cell{X: at.X + sign(to.X-at.X), Z: at.Z + sign(to.Z-at.Z)}
		if c != at && c.X >= 0 && c.Z >= 0 && !slices.Contains(out, c) {
			out = append(out, c)
		}
	}
	return out
}

// animalChoke is the layout's choke when animals body-block it: a
// manhunter pack with no exploder, or a fight whose live hostiles are all
// pikemen.
func animalChoke(view CombatView) *domain.Cell {
	layout, ok := view.Layout.Value()
	if !ok {
		return nil
	}
	choke, ok := layout.Choke.Value()
	if !ok {
		return nil
	}
	if ManhunterPack(view) && len(liveExploders(view)) == 0 {
		return &choke
	}
	threats := rankThreats(view)
	if len(threats) == 0 {
		return nil
	}
	for _, h := range threats {
		if !strings.HasPrefix(h.Kind, "Mech_Pikeman") {
			return nil
		}
	}
	return &choke
}

// meleeRaiders are the live humanlike hostiles known to carry no ranged
// weapon, with a known position, by id, and their cells.
func meleeRaiders(view CombatView) ([]domain.PawnID, []domain.Cell) {
	melee := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		ranged, known := t.RangedEquipped.Value()
		if !t.Building && positive(t.Humanlike) && known && !ranged && !positive(t.Dead) && !positive(t.Downed) {
			melee[domain.PawnID(t.ID)] = true
		}
	}
	down := downPawns(view)
	var ids []domain.PawnID
	var cells []domain.Cell
	for _, t := range view.Positional {
		c, ok := t.Position.Value()
		if id := domain.PawnID(t.ID); ok && melee[id] && !down[id] {
			ids, cells = append(ids, id), append(cells, c)
		}
	}
	return ids, cells
}

// ReasonEnrage is a gunner's shot at a wild animal near the raiders:
// hurt, it turns on whoever is near it.
const ReasonEnrage CombatOrderReason = "enrage"

// Enrage ranges, squared: a wild animal within 10 cells of a live
// raider and beyond 20 of every live colonist is shot.
const (
	enrageNear = 10 * 10
	enrageFar  = 20 * 20
)

// enrageTarget is the first wild animal by id that is unhurt, near a live
// humanlike raider and away from every live colonist; the mirror sends
// only wild predators and large animals near hostiles.
func enrageTarget(view CombatView) (CombatPawnState, bool) {
	humanlike := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		if !t.Building && positive(t.Humanlike) && !positive(t.Dead) && !positive(t.Downed) {
			humanlike[domain.PawnID(t.ID)] = true
		}
	}
	down := downPawns(view)
	var raiders, ours []domain.Cell
	for _, t := range view.Positional {
		if id := domain.PawnID(t.ID); humanlike[id] && !down[id] {
			c, ok := t.Position.Value()
			if !ok {
				continue
			}
			raiders = append(raiders, c)
		}
	}
	if len(raiders) == 0 {
		return CombatPawnState{}, false
	}
	for _, d := range view.Defenders {
		for _, p := range view.Pawns {
			if c, ok := p.Cell.Value(); ok && p.ID == d.ID && !p.Dead && !p.Downed {
				ours = append(ours, c)
			}
		}
	}
	var best CombatPawnState
	found := false
	for _, p := range view.Pawns {
		at, ok := p.Cell.Value()
		if h, known := p.Health.Value(); !ok || !p.Wild || p.Dead || p.Downed || known && h < 1 {
			continue
		}
		if distance2(at, raiders[nearestIndex(at, raiders)]) > enrageNear {
			continue
		}
		if len(ours) > 0 && distance2(at, ours[nearestIndex(at, ours)]) <= enrageFar {
			continue
		}
		if !found || p.ID < best.ID {
			best, found = p, true
		}
	}
	return best, found
}

// enrageWild has the nearest live ranged role shoot the enrage target
// with the attack order, in place of that role's own order.
func enrageWild(view CombatView, orders []CombatOrder, roles []CombatRole, orderable map[domain.PawnID]bool, state map[domain.PawnID]CombatPawnState) []CombatOrder {
	wild, ok := enrageTarget(view)
	if !ok {
		return orders
	}
	at, _ := wild.Cell.Value()
	var gunner domain.PawnID
	bestD := int64(-1)
	for _, r := range roles {
		s := state[r.Pawn]
		c, known := s.Cell.Value()
		if !r.Ranged || !orderable[r.Pawn] || !known || s.Dead || s.Downed {
			continue
		}
		if d := distance2(at, c); bestD < 0 || d < bestD {
			gunner, bestD = r.Pawn, d
		}
	}
	if gunner == "" || state[gunner].Target == wild.ID {
		return orders
	}
	orders = slices.DeleteFunc(orders, func(o CombatOrder) bool { return o.Pawn == gunner })
	return append(orders, CombatOrder{Pawn: gunner, Kind: OrderAttack, Target: wild.ID, Reason: ReasonEnrage})
}
