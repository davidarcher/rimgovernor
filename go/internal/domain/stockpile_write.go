package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

const (
	ZoneCellEditAction   ActionKind = "zone_cell_edit"
	StockpilePatchAction ActionKind = "stockpile_patch"
)

// CellEditMode is native EditZoneCells' direction.
type CellEditMode string

const (
	AddZoneCells    CellEditMode = "add"
	RemoveZoneCells CellEditMode = "remove"
)

// ZoneCellEdit grows or shrinks one exact zone (native EditZoneCells).
// The zone keeps its native id, so a zone_create claim on it survives the edit.
// Cells are canonical (sorted, deduplicated, nonempty); the result must stay
// contiguous (native refuses a split).
type ZoneCellEdit struct {
	zone  string
	mode  CellEditMode
	cells string
}

func NewZoneCellEdit(zone string, mode CellEditMode, cells []Cell) (ZoneCellEdit, error) {
	if !validID(zone) || mode != AddZoneCells && mode != RemoveZoneCells || len(cells) == 0 {
		return ZoneCellEdit{}, errors.New("invalid zone cell edit")
	}
	rows := append([]Cell(nil), cells...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].X < rows[j].X || rows[i].X == rows[j].X && rows[i].Z < rows[j].Z })
	for i, c := range rows {
		if c.X < 0 || c.Z < 0 || i > 0 && rows[i-1] == c {
			return ZoneCellEdit{}, errors.New("invalid zone cell edit cell")
		}
	}
	data, _ := json.Marshal(rows)
	return ZoneCellEdit{zone, mode, string(data)}, nil
}
func (e ZoneCellEdit) Zone() string       { return e.zone }
func (e ZoneCellEdit) Mode() CellEditMode { return e.mode }
func (e ZoneCellEdit) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(e.cells), &cells)
	return cells
}

func NewZoneCellEditAction(id ActionID, e ZoneCellEdit) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewZoneCellEdit(e.zone, e.mode, e.Cells())
	if err != nil || canonical != e {
		return Action{}, errors.New("invalid zone cell edit")
	}
	return Action{id: id, kind: ZoneCellEditAction, zoneCellEdit: e}, nil
}
func (a Action) ZoneCellEdit() (ZoneCellEdit, bool) {
	return a.zoneCellEdit, a.kind == ZoneCellEditAction
}

// StorageTargetKind says what a StockpilePatch's target id names.
type StorageTargetKind string

const (
	StorageZoneTarget     StorageTargetKind = "zone"
	StorageBuildingTarget StorageTargetKind = "building"
)

// StockpilePatch replaces one storage target's priority and filter (native
// PatchStockpile): a stockpile zone or a player storage building such as a
// shelf. The filter's base preset resets
// the target's filter, so the patch is a full replacement. Role is the
// planner role key the patch claims the target for; empty is untagged.
type StockpilePatch struct {
	target   string
	kind     StorageTargetKind
	filter   StockpileFilter
	priority StockpilePriority
	role     string
}

func NewStockpilePatch(kind StorageTargetKind, target string, filter StockpileFilter, priority StockpilePriority, role string) (StockpilePatch, error) {
	canonical, err := ReconstructStockpileFilter(filter)
	if kind != StorageZoneTarget && kind != StorageBuildingTarget || !validID(target) || err != nil || canonical != filter || !validStockpilePriority(priority) || role != "" && (!validID(role) || len(role) > 128) {
		return StockpilePatch{}, errors.New("invalid stockpile patch")
	}
	return StockpilePatch{target, kind, filter, priority, role}, nil
}
func (p StockpilePatch) Target() string                { return p.target }
func (p StockpilePatch) TargetKind() StorageTargetKind { return p.kind }
func (p StockpilePatch) Filter() StockpileFilter       { return p.filter }
func (p StockpilePatch) Priority() StockpilePriority   { return p.priority }
func (p StockpilePatch) Role() string                  { return p.role }

func NewStockpilePatchAction(id ActionID, p StockpilePatch) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewStockpilePatch(p.kind, p.target, p.filter, p.priority, p.role)
	if err != nil || canonical != p {
		return Action{}, errors.New("invalid stockpile patch")
	}
	return Action{id: id, kind: StockpilePatchAction, stockpilePatch: p}, nil
}
func (a Action) StockpilePatch() (StockpilePatch, bool) {
	return a.stockpilePatch, a.kind == StockpilePatchAction
}
