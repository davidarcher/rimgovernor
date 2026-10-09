package policy

import (
	"slices"
	"strconv"
)

// ThingCategory is what a Thing is (the mirror wire's ThingCategory).
// Spawned pawns are never listed.
type ThingCategory uint8

const (
	ThingItem ThingCategory = iota + 1
	ThingBuilding
	ThingPlant
	ThingFilth
	ThingCorpse
	ThingOther
)

// ThingFaction is whose a Thing is.
type ThingFaction uint8

const (
	FactionNone ThingFaction = iota
	FactionPlayer
	FactionNeutral
	FactionHostile
)

// ThingFlags is a Thing's predicate bitset.
type ThingFlags uint32

const (
	FlagEdifice ThingFlags = 1 << iota
	FlagBlueprint
	FlagFrame
	FlagImpassable
	FlagHoldsRoof
	FlagDeconstructible
	FlagMinifiable
	FlagClaimable
	FlagDesignated
	FlagForbidden
	FlagHaulable
	FlagAncientDanger
	FlagNaturalRock

	// FlagsKnown is every defined bit; a wire value with any other is refused.
	FlagsKnown = FlagNaturalRock<<1 - 1
)

// CorpseClass is what a corpse was.
type CorpseClass uint8

const (
	CorpseOther CorpseClass = iota
	CorpseHumanlike
	CorpseAnimal
)

// Material is a count of one def: a blueprint's or frame's owed materials.
type Material struct {
	Def   string
	Count uint32
}

// PlantState is a plant's state.
type PlantState struct {
	Growth   float32
	Blighted bool
}

// CorpseState is a corpse's state.
type CorpseState struct {
	Class CorpseClass
	Rot   float32
}

// Thing is one thing on a cell: a header every category shares and
// the typed state of its category. Only the state matching Category is
// meaningful (Building is non-nil exactly for ThingBuilding).
type Thing struct {
	Def      string
	Category ThingCategory
	Faction  ThingFaction
	Flags    ThingFlags
	ID       uint64
	Count    uint32

	Plant             PlantState
	Corpse            CorpseState
	FilthThickness    uint32
	ItemDeterioration float32
	Building          *BuildingState
}

// BuildingState is a building, blueprint or frame's state.
type BuildingState struct {
	HitPoints uint32
	Burning   bool
	Reserved  bool
	// Needed is the materials still owed; Casket the defs a casket holds.
	Needed []Material `json:",omitempty"`
	Casket []string   `json:",omitempty"`
}

// LoadID is the native entity id of a listed thing: vanilla's
// Thing.GetUniqueLoadID, "Thing_" + defName + thingIDNumber. The one Go builder
// of the form; census rows and the mirror's things resolve through RefIndex.Thing
// by it.
func (t Thing) LoadID() string { return "Thing_" + t.Def + strconv.FormatUint(t.ID, 10) }

// Has reports whether every flag in f is set.
func (t Thing) Has(f ThingFlags) bool { return t.Flags&f == f }

// Equal reports whether two things are identical, state included.
func (t Thing) Equal(o Thing) bool {
	a, b := t.Building, o.Building
	t.Building, o.Building = nil, nil
	if t != o || (a == nil) != (b == nil) {
		return false
	}
	return a == nil || a.HitPoints == b.HitPoints && a.Burning == b.Burning && a.Reserved == b.Reserved &&
		slices.Equal(a.Needed, b.Needed) && slices.Equal(a.Casket, b.Casket)
}

// Equal reports whether two site cells are identical, things included
// (SiteCell is not comparable with == once it holds a list).
func (c SiteCell) Equal(o SiteCell) bool {
	return c.Cell == o.Cell && c.Walkable == o.Walkable && c.Zone == o.Zone && c.Roofed == o.Roofed &&
		c.Indoors == o.Indoors && c.SupportsLight == o.SupportsLight && c.StorageEmpty == o.StorageEmpty && c.Doorway == o.Doorway &&
		c.Fertility == o.Fertility && c.Polluted == o.Polluted && c.Glow == o.Glow && c.Roof == o.Roof && c.ZoneID == o.ZoneID &&
		c.Room == o.Room && c.Terrain == o.Terrain && c.InHome == o.InHome &&
		c.FoundationAffordances == o.FoundationAffordances && c.SnowDepth == o.SnowDepth && c.TopLayerRemovable == o.TopLayerRemovable &&
		ThingsEqual(c.Things, o.Things)
}

// ThingsEqual reports whether two thing lists are identical.
func ThingsEqual(a, b []Thing) bool { return slices.EqualFunc(a, b, Thing.Equal) }

// edifice is the cell's edifice: the thing the native edifice grid
// holds there.
func (c SiteCell) edifice() (Thing, bool) {
	for _, t := range c.Things {
		if t.Has(FlagEdifice) {
			return t, true
		}
	}
	return Thing{}, false
}

// Occupied reports a building, blueprint or frame on the cell: the
// native CellOccupied. Plants, items and filth never occupy a cell.
func (c SiteCell) Occupied() bool {
	for _, t := range c.Things {
		if t.Flags&(FlagEdifice|FlagBlueprint|FlagFrame) != 0 {
			return true
		}
	}
	return false
}

// NaturalRock reports the cell's edifice is natural rock: a
// planned ring on it keeps it as wall; inside the room plan dig mines it.
func (c SiteCell) NaturalRock() bool {
	t, ok := c.edifice()
	return ok && t.Has(FlagNaturalRock)
}

// Cleared is the cell with its occupying things (edifice, blueprints, frames)
// removed, for offering ground as unoccupied whatever the census says of it.
// It copies the list: the cell's own is a view into a shared slab.
func (c SiteCell) Cleared() SiteCell {
	things := make([]Thing, 0, len(c.Things))
	for _, t := range c.Things {
		if t.Flags&(FlagEdifice|FlagBlueprint|FlagFrame) == 0 {
			things = append(things, t)
		}
	}
	c.Things = things
	return c
}

// OccupantThings is the thing list of a cell holding an unremarkable
// edifice when on, none otherwise: the tests' stand-in for the old occupied
// flag.
func OccupantThings(on bool) []Thing {
	if !on {
		return nil
	}
	return []Thing{{Def: "Wall", Category: ThingBuilding, Flags: FlagEdifice | FlagImpassable, Count: 1, Building: &BuildingState{}}}
}

// RockThings is the thing list of a cell of natural rock when on, none
// otherwise (tests).
func RockThings(on bool) []Thing {
	if !on {
		return nil
	}
	return []Thing{{Def: "Granite", Category: ThingBuilding, Flags: FlagEdifice | FlagImpassable | FlagNaturalRock, Count: 1, Building: &BuildingState{}}}
}

// SetOccupied makes the cell hold an unremarkable edifice, or none of its
// occupants when off (tests). It replaces the list, never edits it.
func (c *SiteCell) SetOccupied(on bool) {
	c.Things = append(c.Cleared().Things, OccupantThings(on)...)
}

// SetNaturalRock makes the cell natural rock, or removes its edifice when
// off (tests).
func (c *SiteCell) SetNaturalRock(on bool) {
	c.Things = append(c.Cleared().Things, RockThings(on)...)
}

// PlayerEdifice names the definition of the player-owned edifice on the
// cell, empty for none: a ring of that wall kind stands on it.
func (c SiteCell) PlayerEdifice() string {
	if t, ok := c.edifice(); ok && t.Faction == FactionPlayer {
		return t.Def
	}
	return ""
}

// Ruin reports an unowned edifice the player may deconstruct, outside any
// ancient danger.
func (c SiteCell) Ruin() bool {
	t, ok := c.edifice()
	return ok && t.Faction != FactionPlayer && t.Has(FlagDeconstructible) && !t.Has(FlagAncientDanger)
}

// ClaimableRuin names the definition of a ruin the player may claim, empty
// for none: a ring of that wall kind claims it and keeps it as wall
// instead of clearing it.
func (c SiteCell) ClaimableRuin() string {
	if t, ok := c.edifice(); ok && c.Ruin() && t.Has(FlagClaimable) {
		return t.Def
	}
	return ""
}

// RuinHold is the hold on the unowned building covering the cell that a
// claim honours, empty for none: "ancient_danger", or "casket" for a
// casket still holding contents. A ring claims a ruin only where the claim
// would act on it.
func (c SiteCell) RuinHold() string {
	hold := ""
	for _, t := range c.Things {
		if t.Category != ThingBuilding || t.Faction == FactionPlayer || t.Building == nil || t.Has(FlagBlueprint) || t.Has(FlagFrame) {
			continue
		}
		switch {
		case t.Has(FlagAncientDanger):
			return "ancient_danger"
		case len(t.Building.Casket) > 0:
			hold = "casket"
		}
	}
	return hold
}
