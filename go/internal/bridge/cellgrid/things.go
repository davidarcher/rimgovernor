package cellgrid

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
)

// The per-cell thing list. A Grid holds it CSR style: offsets (one
// per cell plus one) cut a single slab of decoded records into each cell's
// list, and a record's building state and its owed-material and casket
// lists point into three more slabs, so a decode allocates a handful of
// slices however many things the map holds. Records carry their def as a
// string shared with the wire message, not as a table index. A store is
// never changed once built; a grid whose delta leaves the lists alone
// shares its base's store.
type thingStore struct {
	offsets   []uint32 // len cells+1
	slab      []policy.Thing
	buildings []policy.BuildingState
	mats      []policy.Material
	casket    []string
}

// at is cell j's list: a view into the slab, nil when empty.
func (s *thingStore) at(j int) []policy.Thing {
	if s == nil {
		return nil
	}
	lo, hi := s.offsets[j], s.offsets[j+1]
	if lo == hi {
		return nil
	}
	return s.slab[lo:hi:hi]
}

// thingBuilder lays a store out cell by cell.
type thingBuilder struct {
	s thingStore
	// per building appended: where its needed and casket entries sit.
	spans [][4]int
}

func newThingBuilder(cells, things int) *thingBuilder {
	b := &thingBuilder{}
	b.s.offsets = make([]uint32, 1, cells+1)
	b.s.slab = make([]policy.Thing, 0, things)
	return b
}

// add appends t to the open cell, copying its building state into the
// slabs. The record's Building stays pointing at the caller's state until
// finish repoints it.
func (b *thingBuilder) add(t policy.Thing) {
	if bs := t.Building; bs != nil {
		b.spans = append(b.spans, [4]int{len(b.s.mats), len(b.s.mats) + len(bs.Needed), len(b.s.casket), len(b.s.casket) + len(bs.Casket)})
		b.s.mats = append(b.s.mats, bs.Needed...)
		b.s.casket = append(b.s.casket, bs.Casket...)
		b.s.buildings = append(b.s.buildings, policy.BuildingState{HitPoints: bs.HitPoints, Burning: bs.Burning, Reserved: bs.Reserved})
	}
	b.s.slab = append(b.s.slab, t)
}

func (b *thingBuilder) endCell() { b.s.offsets = append(b.s.offsets, uint32(len(b.s.slab))) }

// finish is the store, empty (nil) when no cell holds a thing.
func (b *thingBuilder) finish() *thingStore {
	if len(b.s.slab) == 0 {
		return nil
	}
	k := 0
	for i := range b.s.slab {
		if b.s.slab[i].Building == nil {
			continue
		}
		sp := b.spans[k]
		bs := &b.s.buildings[k]
		if sp[1] > sp[0] {
			bs.Needed = b.s.mats[sp[0]:sp[1]:sp[1]]
		}
		if sp[3] > sp[2] {
			bs.Casket = b.s.casket[sp[2]:sp[3]:sp[3]]
		}
		b.s.slab[i].Building = bs
		k++
	}
	return &b.s
}

// thingCategories maps the wire's category to the model's, and the state
// each requires.
var thingCategories = map[mp.ThingCategory]policy.ThingCategory{
	mp.ThingCategory_THING_CATEGORY_ITEM:     policy.ThingItem,
	mp.ThingCategory_THING_CATEGORY_BUILDING: policy.ThingBuilding,
	mp.ThingCategory_THING_CATEGORY_PLANT:    policy.ThingPlant,
	mp.ThingCategory_THING_CATEGORY_FILTH:    policy.ThingFilth,
	mp.ThingCategory_THING_CATEGORY_CORPSE:   policy.ThingCorpse,
	mp.ThingCategory_THING_CATEGORY_OTHER:    policy.ThingOther,
}

func finite32(x float32) bool { return !math.IsNaN(float64(x)) && !math.IsInf(float64(x), 0) }

// decodeThing is one wire thing as a model record, its strings resolved
// against table; the state must be the one its category carries.
func decodeThing(w *mp.Thing, table []string) (policy.Thing, error) {
	str := func(k uint32) (string, error) {
		if k == 0 || int(k) > len(table) {
			return "", contract("mirror cell grid thing string index out of range")
		}
		return table[k-1], nil
	}
	def, err := str(w.GetDef())
	if err != nil {
		return policy.Thing{}, err
	}
	cat, ok := thingCategories[w.GetCategory()]
	if !ok {
		return policy.Thing{}, contract("mirror cell grid thing category")
	}
	if w.GetFaction() < 0 || w.GetFaction() > mp.ThingFaction_THING_FACTION_HOSTILE {
		return policy.Thing{}, contract("mirror cell grid thing faction")
	}
	if policy.ThingFlags(w.GetFlags())&^policy.FlagsKnown != 0 {
		return policy.Thing{}, contract("mirror cell grid thing flags")
	}
	t := policy.Thing{Def: def, Category: cat, Faction: policy.ThingFaction(w.GetFaction()), Flags: policy.ThingFlags(w.GetFlags()), ID: w.GetId(), Count: w.GetCount()}
	mismatch := contract("mirror cell grid thing state does not match its category")
	switch state := w.State.(type) {
	case nil:
		if cat != policy.ThingOther {
			return policy.Thing{}, mismatch
		}
	case *mp.Thing_Plant:
		if cat != policy.ThingPlant || state.Plant == nil || !finite32(state.Plant.GetGrowth()) {
			return policy.Thing{}, mismatch
		}
		t.Plant = policy.PlantState{Growth: state.Plant.GetGrowth(), Blighted: state.Plant.GetBlighted()}
	case *mp.Thing_Corpse:
		if cat != policy.ThingCorpse || state.Corpse == nil || !finite32(state.Corpse.GetRot()) || state.Corpse.GetClass() < 0 || state.Corpse.GetClass() > mp.CorpseClass_CORPSE_CLASS_ANIMAL {
			return policy.Thing{}, mismatch
		}
		t.Corpse = policy.CorpseState{Class: policy.CorpseClass(state.Corpse.GetClass()), Rot: state.Corpse.GetRot()}
	case *mp.Thing_Filth:
		if cat != policy.ThingFilth || state.Filth == nil {
			return policy.Thing{}, mismatch
		}
		t.FilthThickness = state.Filth.GetThickness()
	case *mp.Thing_Item:
		if cat != policy.ThingItem || state.Item == nil || !finite32(state.Item.GetDeterioration()) {
			return policy.Thing{}, mismatch
		}
		t.ItemDeterioration = state.Item.GetDeterioration()
	case *mp.Thing_Building:
		if cat != policy.ThingBuilding || state.Building == nil {
			return policy.Thing{}, mismatch
		}
		b := state.Building
		bs := &policy.BuildingState{HitPoints: b.GetHitPoints(), Burning: b.GetBurning(), Reserved: b.GetReserved()}
		for _, n := range b.GetNeeded() {
			d, err := str(n.GetDef())
			if err != nil {
				return policy.Thing{}, err
			}
			bs.Needed = append(bs.Needed, policy.Material{Def: d, Count: n.GetCount()})
		}
		for _, k := range b.GetCasket() {
			d, err := str(k)
			if err != nil {
				return policy.Thing{}, err
			}
			bs.Casket = append(bs.Casket, d)
		}
		t.Building = bs
	default:
		return policy.Thing{}, mismatch
	}
	return t, nil
}

// applyThings lays a wire thing list over base (nil: no things), the cell
// lists it names replacing the base's. A nil list leaves base as it was.
func applyThings(base *thingStore, list *mp.ThingList, n int, table []string) (*thingStore, error) {
	if list == nil {
		return base, nil
	}
	cells, offsets, things := list.GetCells(), list.GetOffsets(), list.GetThings()
	if len(offsets) != len(cells)+1 || offsets[0] != 0 || int(offsets[len(offsets)-1]) != len(things) {
		return nil, contract("mirror cell grid thing offsets")
	}
	last := -1
	for p, at := range cells {
		if int(at) <= last || int(at) >= n {
			return nil, contract("mirror cell grid thing cell index")
		}
		last = int(at)
		if offsets[p+1] < offsets[p] {
			return nil, contract("mirror cell grid thing offsets not monotonic")
		}
	}
	if len(cells) == 0 {
		return base, nil
	}
	decoded := make([]policy.Thing, len(things))
	for i, w := range things {
		t, err := decodeThing(w, table)
		if err != nil {
			return nil, err
		}
		decoded[i] = t
	}
	total := len(decoded)
	if base != nil {
		total += len(base.slab)
	}
	b := newThingBuilder(n, total)
	p := 0
	for j := 0; j < n; j++ {
		if p < len(cells) && int(cells[p]) == j {
			for _, t := range decoded[offsets[p]:offsets[p+1]] {
				b.add(t)
			}
			p++
		} else {
			for _, t := range base.at(j) {
				b.add(t)
			}
		}
		b.endCell()
	}
	return b.finish(), nil
}

// wireThing is t as a wire thing, its strings interned by index.
func wireThing(t policy.Thing, index func(string) uint32) *mp.Thing {
	w := &mp.Thing{Def: index(t.Def), Category: mp.ThingCategory_THING_CATEGORY_UNSPECIFIED, Faction: mp.ThingFaction(t.Faction), Flags: uint32(t.Flags), Id: t.ID, Count: t.Count}
	for wire, cat := range thingCategories {
		if cat == t.Category {
			w.Category = wire
		}
	}
	switch t.Category {
	case policy.ThingPlant:
		w.State = &mp.Thing_Plant{Plant: &mp.PlantState{Growth: t.Plant.Growth, Blighted: t.Plant.Blighted}}
	case policy.ThingCorpse:
		w.State = &mp.Thing_Corpse{Corpse: &mp.CorpseState{Class: mp.CorpseClass(t.Corpse.Class), Rot: t.Corpse.Rot}}
	case policy.ThingFilth:
		w.State = &mp.Thing_Filth{Filth: &mp.FilthState{Thickness: t.FilthThickness}}
	case policy.ThingItem:
		w.State = &mp.Thing_Item{Item: &mp.ItemState{Deterioration: t.ItemDeterioration}}
	case policy.ThingBuilding:
		b := &mp.BuildingState{}
		if t.Building != nil {
			b.HitPoints, b.Burning, b.Reserved = t.Building.HitPoints, t.Building.Burning, t.Building.Reserved
			for _, m := range t.Building.Needed {
				b.Needed = append(b.Needed, &mp.MaterialNeed{Def: index(m.Def), Count: m.Count})
			}
			for _, d := range t.Building.Casket {
				b.Casket = append(b.Casket, index(d))
			}
		}
		w.State = &mp.Thing_Building{Building: b}
	}
	return w
}

// wireThings is g's thing lists as a wire list over base (nil: a keyframe,
// every cell without things): the cells whose list differs from base's,
// replaced in full. nil when base is set and nothing differs.
func (g *Grid) wireThings(base *Grid, index func(string) uint32) *mp.ThingList {
	var held *thingStore
	if base != nil {
		held = base.things
	}
	list := &mp.ThingList{Offsets: []uint32{0}}
	n := int(g.Rect.Width) * int(g.Rect.Height)
	if g.things == nil && held == nil {
		if base != nil {
			return nil
		}
		return list
	}
	for j := 0; j < n; j++ {
		now := g.things.at(j)
		if policy.ThingsEqual(now, held.at(j)) {
			continue
		}
		list.Cells = append(list.Cells, uint32(j))
		for _, t := range now {
			list.Things = append(list.Things, wireThing(t, index))
		}
		list.Offsets = append(list.Offsets, uint32(len(list.Things)))
	}
	if base != nil && len(list.Cells) == 0 {
		return nil
	}
	return list
}
