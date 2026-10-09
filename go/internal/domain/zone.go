package domain

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"
)

const ZoneCreateAction ActionKind = "zone_create"

type ZoneKind string

const (
	GrowingZone   ZoneKind = "growing"
	StockpileZone ZoneKind = "stockpile"
	FishingZone   ZoneKind = "fishing"
)

// StockpilePriority spans vanilla's five storage priorities: a
// Normal general store holds the colony's stock, Important/Critical working
// stockpiles at benches and the kitchen pull from it, and Low is the dump.
type StockpilePriority string

const (
	CriticalPriority  StockpilePriority = "critical"
	ImportantPriority StockpilePriority = "important"
	PreferredPriority StockpilePriority = "preferred"
	// NormalPriority is the general store's: any working stockpile above it
	// pulls its allowed things out of the store.
	NormalPriority StockpilePriority = "normal"
	// LowPriority is vanilla's dumping-stockpile priority: the chunk dump a
	// clearance method places so any other store the player or a later
	// method admits for the same things wins the haul.
	LowPriority StockpilePriority = "low"
)

// ZoneCreate is an exact growing/fishing footprint or a stockpile rectangle
// drag. RimWorld resolves the stockpile's usable cells and connected zones.
// Role is the stable key a stockpile planner claims the
// zone by (e.g. "general", "ingredients:<benchID>", "dump:worn"); it
// travels with the zone_create receipt into store.OwnedZone. Empty is a
// legacy role-less claim.
type ZoneCreate struct {
	kind      ZoneKind
	crop      string
	priority  StockpilePriority
	cells     string
	filter    StockpileFilter
	role      string
	extendID  string
	rectangle GroundRect
}

func canonicalConnectedCells(cells []Cell) (string, error) {
	if len(cells) == 0 {
		return "", errors.New("invalid zone configuration")
	}
	rows := append([]Cell(nil), cells...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].X < rows[j].X || rows[i].X == rows[j].X && rows[i].Z < rows[j].Z })
	seen := map[Cell]bool{}
	for _, cell := range rows {
		if cell.X < 0 || cell.Z < 0 || seen[cell] {
			return "", errors.New("invalid zone cell")
		}
		seen[cell] = true
	}
	reached := map[Cell]bool{rows[0]: true}
	queue := []Cell{rows[0]}
	for len(queue) > 0 {
		cell := queue[0]
		queue = queue[1:]
		for _, delta := range []Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			next := Cell{X: cell.X + delta.X, Z: cell.Z + delta.Z}
			if seen[next] && !reached[next] {
				reached[next] = true
				queue = append(queue, next)
			}
		}
	}
	if len(reached) != len(rows) {
		return "", errors.New("zone footprint disconnected")
	}
	data, _ := json.Marshal(rows)
	return string(data), nil
}

func NewZoneCreate(kind ZoneKind, crop string, cells []Cell) (ZoneCreate, error) {
	if kind != GrowingZone || !validID(crop) {
		return ZoneCreate{}, errors.New("invalid zone configuration")
	}
	data, err := canonicalConnectedCells(cells)
	if err != nil {
		return ZoneCreate{}, err
	}
	return ZoneCreate{kind: kind, crop: crop, cells: data}, nil
}

// NewFishingZone uses ordinary native fishing with a conservative population
// floor. Its footprint provides access; it is not a daily catch quota.
func NewFishingZone(cells []Cell) (ZoneCreate, error) {
	data, err := canonicalConnectedCells(cells)
	if err != nil {
		return ZoneCreate{}, err
	}
	return ZoneCreate{kind: FishingZone, cells: data}, nil
}

const FishingPopulationFloor = 0.6

// TreeLatticePitch is the sowing lattice of a blockAdjacentSow plant (every
// tree): the native sower offers only cells whose offsets from the growing
// zone's minimum corner are both multiples of the pitch, so a zone holds one
// tree per TreeLatticePitch^2 cells and fills completely. Mirrors
// TreeLatticeSowing.Pitch in the native mod (a test compares them).
const TreeLatticePitch = 2

// TreeCellsPerTree is the zone area one lattice tree takes.
const TreeCellsPerTree = TreeLatticePitch * TreeLatticePitch

// NewFishingZoneExtension names the exact existing zone and the complete final
// footprint. Native admission requires a strict superset in the same body.
func NewFishingZoneExtension(zoneID string, cells []Cell) (ZoneCreate, error) {
	if !validID(zoneID) {
		return ZoneCreate{}, errors.New("invalid fishing zone identity")
	}
	z, err := NewFishingZone(cells)
	z.extendID = zoneID
	return z, err
}

func (z ZoneCreate) ExtendZoneID() string { return z.extendID }

func validStockpilePriority(p StockpilePriority) bool {
	switch p {
	case CriticalPriority, ImportantPriority, PreferredPriority, NormalPriority, LowPriority:
		return true
	}
	return false
}

// NewFilteredStockpileZone requests a rectangle drag with a canonical filter.
func NewFilteredStockpileZone(filter StockpileFilter, priority StockpilePriority, rectangle GroundRect) (ZoneCreate, error) {
	canonical, err := ReconstructStockpileFilter(filter)
	if err != nil || canonical != filter || !validStockpilePriority(priority) {
		return ZoneCreate{}, errors.New("invalid stockpile zone configuration")
	}
	if rectangle.Origin.X < 0 || rectangle.Origin.Z < 0 || rectangle.Width <= 0 || rectangle.Height <= 0 || int64(rectangle.Origin.X)+int64(rectangle.Width) > math.MaxInt32 || int64(rectangle.Origin.Z)+int64(rectangle.Height) > math.MaxInt32 {
		return ZoneCreate{}, errors.New("invalid stockpile rectangle")
	}
	return ZoneCreate{kind: StockpileZone, priority: priority, rectangle: rectangle, filter: filter}, nil
}

// WithRole tags a stockpile with the role key its planner claims it by.
func (z ZoneCreate) WithRole(role string) (ZoneCreate, error) {
	if z.kind != StockpileZone || !validID(role) || len(role) > 128 {
		return ZoneCreate{}, errors.New("invalid stockpile role")
	}
	z.role = role
	return z, nil
}

// ReconstructZone rebuilds a canonical ZoneCreate from a value of unknown
// provenance (a persisted row, a wire readback) by dispatching on its kind to
// the family-specific constructor, exactly like every other closed Action
// variant's canonical-equality check.
func ReconstructZone(z ZoneCreate) (ZoneCreate, error) {
	switch z.kind {
	case FishingZone:
		if z.extendID != "" {
			return NewFishingZoneExtension(z.extendID, z.Cells())
		}
		return NewFishingZone(z.Cells())
	case GrowingZone:
		return NewZoneCreate(z.kind, z.crop, z.Cells())
	case StockpileZone:
		out, err := NewFilteredStockpileZone(z.filter, z.priority, z.rectangle)
		if err == nil && z.role != "" {
			out, err = out.WithRole(z.role)
		}
		return out, err
	default:
		return ZoneCreate{}, errors.New("unsupported zone kind")
	}
}

func (z ZoneCreate) Kind() ZoneKind              { return z.kind }
func (z ZoneCreate) Crop() string                { return z.crop }
func (z ZoneCreate) Priority() StockpilePriority { return z.priority }

// Cells is the requested ground; stockpile receipts hold actual created cells.
func (z ZoneCreate) Cells() []Cell {
	if z.kind == StockpileZone {
		return z.rectangle.Cells()
	}
	var cells []Cell
	_ = json.Unmarshal([]byte(z.cells), &cells)
	return cells
}

func (z ZoneCreate) Rectangle() GroundRect { return z.rectangle }

func (r GroundRect) Cells() []Cell {
	var cells []Cell
	for x := int32(0); x < r.Width; x++ {
		for z := int32(0); z < r.Height; z++ {
			cells = append(cells, Cell{X: r.Origin.X + x, Z: r.Origin.Z + z})
		}
	}
	return cells
}

// Filter is a stockpile's storage filter; zero for other zone kinds.
func (z ZoneCreate) Filter() StockpileFilter { return z.filter }

// Role is a stockpile's planner role key; empty when untagged.
func (z ZoneCreate) Role() string { return z.role }
func (z ZoneCreate) Label() string {
	switch {
	case z.kind == FishingZone:
		return "Fishing"
	case z.kind == StockpileZone:
		return stockpileLabel(z.filter, z.priority)
	default:
		return "Crops"
	}
}

// stockpileLabel names a stockpile by its filter; the named filters keep the
// labels their retired presets sent native.
func stockpileLabel(f StockpileFilter, priority StockpilePriority) string {
	definitions, allowOnly := f.AllowOnlyDefinitions()
	switch {
	case f == FoodFilter():
		return "Food storage"
	case f == CorpseLarderFilter():
		return "Corpse larder"
	case f == GeneralFilter():
		return "Warehouse"
	case f == OpeningStoreFilter():
		return "General store"
	case f == RawFoodFilter():
		return "Raw food"
	case f == RawMeatFilter():
		return "Raw meat"
	case f == RawVegFilter():
		return "Raw vegetables"
	case f == PerishablesFilter():
		return "Perishables"
	case f == TombCorpsesFilter():
		return "Tomb corpses"
	case f == MorgueCorpsesFilter():
		return "Morgue"
	case IsArmoryFilter(f):
		return "Armory"
	case IsWardrobeFilter(f):
		return "Wardrobe"
	case allowOnly && priority == LowPriority:
		return "Dumping"
	case allowOnly:
		return definitionList(definitions)
	default:
		return "Stockpile"
	}
}

// definitionList names up to three definitions and counts the rest, so a
// label stays short enough for the zone tab.
func definitionList(definitions []string) string {
	const shown = 3
	if len(definitions) <= shown {
		return strings.Join(definitions, ", ")
	}
	return fmt.Sprintf("%s +%d more", strings.Join(definitions[:shown], ", "), len(definitions)-shown)
}

func NewZoneCreateAction(id ActionID, z ZoneCreate) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := ReconstructZone(z)
	if err != nil || canonical != z {
		return Action{}, errors.New("invalid zone value")
	}
	return Action{id: id, kind: ZoneCreateAction, zone: z}, nil
}
func (a Action) ZoneCreate() (ZoneCreate, bool) { return a.zone, a.kind == ZoneCreateAction }
