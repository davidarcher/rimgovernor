package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

const ZoneCreateAction ActionKind = "zone_create"

type ZoneKind string

const (
	GrowingZone   ZoneKind = "growing"
	StockpileZone ZoneKind = "stockpile"
	FishingZone   ZoneKind = "fishing"
)

// StockpilePreset is closed to the configurations the planners dispatch.
// StockpilePriority spans vanilla's five storage priorities (#720): a
// Normal general store holds the colony's stock, Important/Critical working
// stockpiles at benches and the kitchen pull from it, and Low is the dump.
type StockpilePreset string

const (
	FoodPreset         StockpilePreset = "food"
	CorpseLarderPreset StockpilePreset = "corpse_larder"
	// NothingPreset is the allow-list-only filter SecureSupplies' covered
	// storage/supply storeroom fallback uses when no ordinary haul
	// destination exists: everything is disallowed except the explicit
	// definitions named in Allow() (preset='nothing', allow=definitions).
	NothingPreset StockpilePreset = "nothing"
	// GeneralPreset is the catch-all storeroom: every non-perishable
	// storable except chunks (native preset='nonperishables' less the Chunks
	// category). Perishables keep to the food store.
	GeneralPreset StockpilePreset = "general"
)

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

// ZoneCreate owns one bounded connected footprint. Settings are closed variants:
// an explicitly sown crop, or a typed stockpile filter preset/priority, the
// latter optionally paired with a canonical allow-list of definitions.
//
// A stockpile carries its StockpileFilter: the named presets derive it
// (Preset() names which), NewFilteredStockpileZone takes any filter
// (Preset() empty). Role is the stable key a stockpile planner claims the
// zone by (e.g. "general", "ingredients:<benchID>", "dump:worn"); it
// travels with the zone_create receipt into store.OwnedZone. Empty is a
// legacy role-less claim.
type ZoneCreate struct {
	kind     ZoneKind
	crop     string
	preset   StockpilePreset
	priority StockpilePriority
	cells    string
	filter   StockpileFilter
	role     string
	extendID string
}

func canonicalConnectedCells(cells []Cell) (string, error) {
	if len(cells) == 0 || len(cells) > 256 {
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

// canonicalAllowList sorts, deduplicates and bounds an allow-list of
// definitions, mirroring canonicalConnectedCells' role for zone footprints.
// The list is capped at 32 sorted, deduplicated
// definitions before ever reaching the native call.
func canonicalAllowList(definitions []string) (string, error) {
	if len(definitions) == 0 || len(definitions) > 32 {
		return "", errors.New("invalid stockpile allow-list")
	}
	rows := append([]string(nil), definitions...)
	sort.Strings(rows)
	seen := map[string]bool{}
	for _, name := range rows {
		if !validID(name) || seen[name] {
			return "", errors.New("invalid stockpile allow-list definition")
		}
		seen[name] = true
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

func NewStockpileZone(preset StockpilePreset, priority StockpilePriority, cells []Cell) (ZoneCreate, error) {
	if (preset != FoodPreset && preset != CorpseLarderPreset && preset != GeneralPreset) || !validStockpilePriority(priority) {
		return ZoneCreate{}, errors.New("invalid stockpile zone configuration")
	}
	data, err := canonicalConnectedCells(cells)
	if err != nil {
		return ZoneCreate{}, err
	}
	filter := FoodFilter()
	switch preset {
	case CorpseLarderPreset:
		filter = CorpseLarderFilter()
	case GeneralPreset:
		filter = GeneralFilter()
	}
	return ZoneCreate{kind: StockpileZone, preset: preset, priority: priority, cells: data, filter: filter}, nil
}

// NewFilteredStockpileZone is a stockpile with any canonical filter.
func NewFilteredStockpileZone(filter StockpileFilter, priority StockpilePriority, cells []Cell) (ZoneCreate, error) {
	canonical, err := ReconstructStockpileFilter(filter)
	if err != nil || canonical != filter || !validStockpilePriority(priority) {
		return ZoneCreate{}, errors.New("invalid stockpile zone configuration")
	}
	data, err := canonicalConnectedCells(cells)
	if err != nil {
		return ZoneCreate{}, err
	}
	return ZoneCreate{kind: StockpileZone, priority: priority, cells: data, filter: filter}, nil
}

// WithRole tags a stockpile with the role key its planner claims it by.
func (z ZoneCreate) WithRole(role string) (ZoneCreate, error) {
	if z.kind != StockpileZone || !validID(role) || len(role) > 128 {
		return ZoneCreate{}, errors.New("invalid stockpile role")
	}
	z.role = role
	return z, nil
}

// NewAllowListStockpileZone builds the NothingPreset covered-storage/supply
// storeroom fallback variant: an "everything disallowed except this explicit
// definition list" filter, as the covered_storage and
// supply_storeroom fallbacks request from the native zone-cells/CreateZone operation.
// LowPriority marks a dumping stockpile.
func NewAllowListStockpileZone(priority StockpilePriority, allow []string, cells []Cell) (ZoneCreate, error) {
	if !validStockpilePriority(priority) {
		return ZoneCreate{}, errors.New("invalid stockpile zone configuration")
	}
	if _, err := canonicalAllowList(allow); err != nil {
		return ZoneCreate{}, err
	}
	filter, err := AllowOnlyFilter(allow)
	if err != nil {
		return ZoneCreate{}, err
	}
	cellData, err := canonicalConnectedCells(cells)
	if err != nil {
		return ZoneCreate{}, err
	}
	return ZoneCreate{kind: StockpileZone, preset: NothingPreset, priority: priority, cells: cellData, filter: filter}, nil
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
		var out ZoneCreate
		var err error
		switch z.preset {
		case FoodPreset, CorpseLarderPreset, GeneralPreset:
			out, err = NewStockpileZone(z.preset, z.priority, z.Cells())
		case NothingPreset:
			out, err = NewAllowListStockpileZone(z.priority, z.Allow(), z.Cells())
		case "":
			out, err = NewFilteredStockpileZone(z.filter, z.priority, z.Cells())
		default:
			return ZoneCreate{}, errors.New("unsupported stockpile preset")
		}
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
func (z ZoneCreate) Preset() StockpilePreset     { return z.preset }
func (z ZoneCreate) Priority() StockpilePriority { return z.priority }
func (z ZoneCreate) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(z.cells), &cells)
	return cells
}

// Allow is an allow-list stockpile's (NothingPreset) thing definitions.
func (z ZoneCreate) Allow() []string {
	if z.preset != NothingPreset {
		return nil
	}
	var names []string
	for _, s := range z.filter.Allow() {
		names = append(names, s.Name)
	}
	return names
}

// Filter is a stockpile's storage filter; zero for other zone kinds.
func (z ZoneCreate) Filter() StockpileFilter { return z.filter }

// Role is a stockpile's planner role key; empty when untagged.
func (z ZoneCreate) Role() string { return z.role }
func (z ZoneCreate) Label() string {
	switch {
	case z.kind == FishingZone:
		return "RimGovernor fishing"
	case z.kind == StockpileZone && z.preset == CorpseLarderPreset:
		return "RimGovernor corpse larder"
	case z.kind == StockpileZone && z.preset == GeneralPreset:
		return "RimGovernor general store"
	case z.kind == StockpileZone && z.preset == NothingPreset && z.priority == LowPriority:
		return "RimGovernor dumping"
	case z.kind == StockpileZone && z.preset == NothingPreset:
		return "RimGovernor supplies storage"
	case z.kind == StockpileZone && z.preset == "":
		return "RimGovernor stockpile"
	case z.kind == StockpileZone:
		return "RimGovernor food storage"
	default:
		return "RimGovernor crops"
	}
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
