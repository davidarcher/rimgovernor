package policy

import "slices"

// ThingCategory is what a Thing is (the mirror wire's ThingCategory, #2260).
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

	// FlagsKnown is every defined bit; a wire value with any other is refused.
	FlagsKnown = FlagAncientDanger<<1 - 1
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

// Thing is one thing on a cell (#2260): a header every category shares and
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
	return c.Cell == o.Cell && c.Walkable == o.Walkable && c.Occupied == o.Occupied && c.Zone == o.Zone && c.Roofed == o.Roofed &&
		c.Indoors == o.Indoors && c.SupportsLight == o.SupportsLight && c.StorageEmpty == o.StorageEmpty && c.Doorway == o.Doorway &&
		c.Fertility == o.Fertility && c.Polluted == o.Polluted && c.Glow == o.Glow && c.Roof == o.Roof && c.ZoneID == o.ZoneID &&
		c.Room == o.Room && c.NaturalRock == o.NaturalRock && c.Terrain == o.Terrain && c.InHome == o.InHome &&
		c.FoundationAffordances == o.FoundationAffordances && c.SnowDepth == o.SnowDepth && c.TopLayerRemovable == o.TopLayerRemovable &&
		ThingsEqual(c.Things, o.Things)
}

// ThingsEqual reports whether two thing lists are identical.
func ThingsEqual(a, b []Thing) bool { return slices.EqualFunc(a, b, Thing.Equal) }

// edifice is the cell's edifice (#2262): the thing the native edifice grid
// holds there.
func (c SiteCell) edifice() (Thing, bool) {
	for _, t := range c.Things {
		if t.Has(FlagEdifice) {
			return t, true
		}
	}
	return Thing{}, false
}

// PlayerEdifice names the definition of the player-owned edifice on the
// cell, empty for none (#709): a ring of that wall kind stands on it.
func (c SiteCell) PlayerEdifice() string {
	if t, ok := c.edifice(); ok && t.Faction == FactionPlayer {
		return t.Def
	}
	return ""
}

// Ruin reports an unowned edifice the player may deconstruct, outside any
// ancient danger (#709).
func (c SiteCell) Ruin() bool {
	t, ok := c.edifice()
	return ok && t.Faction != FactionPlayer && t.Has(FlagDeconstructible) && !t.Has(FlagAncientDanger)
}

// ClaimableRuin names the definition of a ruin the player may claim, empty
// for none (#718): a ring of that wall kind claims it and keeps it as wall
// instead of clearing it.
func (c SiteCell) ClaimableRuin() string {
	if t, ok := c.edifice(); ok && c.Ruin() && t.Has(FlagClaimable) {
		return t.Def
	}
	return ""
}

// RuinHold is the hold on the unowned building covering the cell that a
// claim honours, empty for none (#718): "ancient_danger", or "casket" for a
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
