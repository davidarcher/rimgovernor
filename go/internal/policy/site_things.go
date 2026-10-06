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
		c.Room == o.Room && c.NaturalRock == o.NaturalRock && c.Ruin == o.Ruin && c.PlayerEdifice == o.PlayerEdifice &&
		c.ClaimableRuin == o.ClaimableRuin && c.RuinHold == o.RuinHold && c.Terrain == o.Terrain && c.InHome == o.InHome &&
		c.FoundationAffordances == o.FoundationAffordances && c.SnowDepth == o.SnowDepth && c.TopLayerRemovable == o.TopLayerRemovable &&
		ThingsEqual(c.Things, o.Things)
}

// ThingsEqual reports whether two thing lists are identical.
func ThingsEqual(a, b []Thing) bool { return slices.EqualFunc(a, b, Thing.Equal) }
