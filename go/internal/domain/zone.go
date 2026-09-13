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
)

// StockpilePreset and StockpilePriority are closed to the food-storage and
// allow-listed covered-supplies configurations this slice dispatches; broader
// presets/priorities remain a future extension once a recurring contract
// needs them.
type StockpilePreset string

const (
	FoodPreset StockpilePreset = "food"
	// NothingPreset is the allow-list-only filter SecureSupplies' covered
	// storage/supply storeroom fallback uses when no ordinary haul
	// destination exists: everything is disallowed except the explicit
	// definitions named in Allow(), mirroring upkeep_storage.py's
	// preset='nothing', allow=definitions native zone request.
	NothingPreset StockpilePreset = "nothing"
)

type StockpilePriority string

const ImportantPriority StockpilePriority = "important"

// ZoneCreate owns one bounded connected footprint. Settings are closed variants:
// an explicitly sown crop, or a typed stockpile filter preset/priority, the
// latter optionally paired with a canonical allow-list of definitions.
type ZoneCreate struct {
	kind     ZoneKind
	crop     string
	preset   StockpilePreset
	priority StockpilePriority
	cells    string
	allow    string
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
// Python's covered_storage caps the same list at 32 sorted, deduplicated
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

func NewStockpileZone(preset StockpilePreset, priority StockpilePriority, cells []Cell) (ZoneCreate, error) {
	if preset != FoodPreset || priority != ImportantPriority {
		return ZoneCreate{}, errors.New("invalid stockpile zone configuration")
	}
	data, err := canonicalConnectedCells(cells)
	if err != nil {
		return ZoneCreate{}, err
	}
	return ZoneCreate{kind: StockpileZone, preset: preset, priority: priority, cells: data}, nil
}

// NewAllowListStockpileZone builds the NothingPreset covered-storage/supply
// storeroom fallback variant: an "everything disallowed except this explicit
// definition list" filter, exactly as upkeep_storage.py's covered_storage and
// supply_storeroom request from the native zone-cells/CreateZone operation.
func NewAllowListStockpileZone(priority StockpilePriority, allow []string, cells []Cell) (ZoneCreate, error) {
	if priority != ImportantPriority {
		return ZoneCreate{}, errors.New("invalid stockpile zone configuration")
	}
	allowData, err := canonicalAllowList(allow)
	if err != nil {
		return ZoneCreate{}, err
	}
	cellData, err := canonicalConnectedCells(cells)
	if err != nil {
		return ZoneCreate{}, err
	}
	return ZoneCreate{kind: StockpileZone, preset: NothingPreset, priority: priority, cells: cellData, allow: allowData}, nil
}

// ReconstructZone rebuilds a canonical ZoneCreate from a value of unknown
// provenance (a persisted row, a wire readback) by dispatching on its kind to
// the family-specific constructor, exactly like every other closed Action
// variant's canonical-equality check.
func ReconstructZone(z ZoneCreate) (ZoneCreate, error) {
	switch z.kind {
	case GrowingZone:
		return NewZoneCreate(z.kind, z.crop, z.Cells())
	case StockpileZone:
		switch z.preset {
		case FoodPreset:
			return NewStockpileZone(z.preset, z.priority, z.Cells())
		case NothingPreset:
			return NewAllowListStockpileZone(z.priority, z.Allow(), z.Cells())
		default:
			return ZoneCreate{}, errors.New("unsupported stockpile preset")
		}
	default:
		return ZoneCreate{}, errors.New("unsupported zone kind")
	}
}

func (z ZoneCreate) Kind() ZoneKind                 { return z.kind }
func (z ZoneCreate) Crop() string                   { return z.crop }
func (z ZoneCreate) Preset() StockpilePreset        { return z.preset }
func (z ZoneCreate) Priority() StockpilePriority    { return z.priority }
func (z ZoneCreate) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(z.cells), &cells)
	return cells
}
func (z ZoneCreate) Allow() []string {
	var names []string
	_ = json.Unmarshal([]byte(z.allow), &names)
	return names
}
func (z ZoneCreate) Label() string {
	switch {
	case z.kind == StockpileZone && z.preset == NothingPreset:
		return "RimGovernor supplies storage"
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
