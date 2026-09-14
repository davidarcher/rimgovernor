package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

const ZoneEditAction ActionKind = "zone_edit"

// ZoneEditOp is a closed discriminator over EditZone's five native
// sub-operations. Only ZoneEditAdd, ZoneEditRemove and ZoneEditDelete are
// constructible in this slice: the receipts.proto SettingsField enum has no
// zone growing-plant/stockpile-priority/preset/filter entries yet, so a
// PatchGrowing (crop) or PatchStockpile (filter) effect cannot be verified
// against evidence today (see EffectEvidence.settings/SettingsEffect and
// bridge/building_temperature.go's buildingTemperatureEffect for the pattern
// this would need). ZoneEditCrop and ZoneEditFilter are declared so the
// closed-enum shape is visible, but no NewZoneEdit* constructor produces
// them; adding that support is deferred until the native contract exposes
// those fields.
type ZoneEditOp string

const (
	ZoneEditAdd    ZoneEditOp = "add"
	ZoneEditRemove ZoneEditOp = "remove"
	ZoneEditDelete ZoneEditOp = "delete"
	ZoneEditCrop   ZoneEditOp = "crop"
	ZoneEditFilter ZoneEditOp = "filter"
)

// ZoneEdit applies one bounded edit to an existing native zone identified by
// zoneID -- an opaque native entity identity bounded against caller-supplied
// inspected-zone facts at proposal time, exactly like WallRemoval.Original()
// -- gated by an already-observed exact snapshot token (before), the same
// discipline BuildingTemperature's before/BeforeToken pattern uses for a
// single-entity CAS-gated settings patch.
type ZoneEdit struct {
	zoneID string
	before string
	op     ZoneEditOp
	cells  string
}

// canonicalEditCells bounds, deduplicates and canonically orders a plain
// cell delta for an add/remove operation. Unlike canonicalConnectedCells,
// this applies no connectivity constraint: EditZoneCells' wire shape is a
// plain Cells list describing cells to add to or remove from an existing
// zone, not a from-scratch footprint.
func canonicalEditCells(cells []Cell) (string, error) {
	if len(cells) == 0 || len(cells) > 1024 {
		return "", errors.New("invalid zone edit cells")
	}
	rows := append([]Cell(nil), cells...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].X < rows[j].X || rows[i].X == rows[j].X && rows[i].Z < rows[j].Z })
	seen := map[Cell]bool{}
	for _, cell := range rows {
		if cell.X < 0 || cell.Z < 0 || seen[cell] {
			return "", errors.New("invalid zone edit cell")
		}
		seen[cell] = true
	}
	data, _ := json.Marshal(rows)
	return string(data), nil
}

func NewZoneEditAdd(zoneID, before string, cells []Cell) (ZoneEdit, error) {
	if !validID(zoneID) || !validID(before) {
		return ZoneEdit{}, errors.New("invalid zone edit identity")
	}
	data, err := canonicalEditCells(cells)
	if err != nil {
		return ZoneEdit{}, err
	}
	return ZoneEdit{zoneID: zoneID, before: before, op: ZoneEditAdd, cells: data}, nil
}

func NewZoneEditRemove(zoneID, before string, cells []Cell) (ZoneEdit, error) {
	if !validID(zoneID) || !validID(before) {
		return ZoneEdit{}, errors.New("invalid zone edit identity")
	}
	data, err := canonicalEditCells(cells)
	if err != nil {
		return ZoneEdit{}, err
	}
	return ZoneEdit{zoneID: zoneID, before: before, op: ZoneEditRemove, cells: data}, nil
}

func NewZoneEditDelete(zoneID, before string) (ZoneEdit, error) {
	if !validID(zoneID) || !validID(before) {
		return ZoneEdit{}, errors.New("invalid zone edit identity")
	}
	return ZoneEdit{zoneID: zoneID, before: before, op: ZoneEditDelete}, nil
}

// ReconstructZoneEdit rebuilds a canonical ZoneEdit from a value of unknown
// provenance by dispatching on its op to the family-specific constructor,
// mirroring domain.ReconstructZone's own dispatch.
func ReconstructZoneEdit(z ZoneEdit) (ZoneEdit, error) {
	switch z.op {
	case ZoneEditAdd:
		return NewZoneEditAdd(z.zoneID, z.before, z.Cells())
	case ZoneEditRemove:
		return NewZoneEditRemove(z.zoneID, z.before, z.Cells())
	case ZoneEditDelete:
		return NewZoneEditDelete(z.zoneID, z.before)
	default:
		return ZoneEdit{}, errors.New("unsupported zone edit operation")
	}
}

func (z ZoneEdit) ZoneID() string      { return z.zoneID }
func (z ZoneEdit) BeforeToken() string { return z.before }
func (z ZoneEdit) Op() ZoneEditOp      { return z.op }
func (z ZoneEdit) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(z.cells), &cells)
	return cells
}
func (z ZoneEdit) Label() string {
	switch z.op {
	case ZoneEditAdd:
		return "RimGovernor zone add"
	case ZoneEditRemove:
		return "RimGovernor zone remove"
	default:
		return "RimGovernor zone delete"
	}
}

func NewZoneEditAction(id ActionID, z ZoneEdit) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := ReconstructZoneEdit(z)
	if err != nil || canonical != z {
		return Action{}, errors.New("invalid zone edit value")
	}
	return Action{id: id, kind: ZoneEditAction, zoneEdit: z}, nil
}
func (a Action) ZoneEdit() (ZoneEdit, bool) { return a.zoneEdit, a.kind == ZoneEditAction }
