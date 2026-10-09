package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// AreaAction creates, edits or deletes one bot-owned allowed area, or edits
// the home area: an AreaIntent on Actions/Apply. A bot-owned area is
// the Area_Allowed labelled with its key; native never touches an
// area the player made.
const AreaAction ActionKind = "area"

// AreaOperation is the edit an AreaIntent applies.
type AreaOperation string

const (
	AreaCreate     AreaOperation = "create"
	AreaSetCells   AreaOperation = "set_cells"
	AreaClearCells AreaOperation = "clear_cells"
	AreaDelete     AreaOperation = "delete"
)

// Area is an immutable, comparable value: the operation, the bot area key
// (empty for the home area) and canonical cells (sorted, deduplicated).
// set_cells and clear_cells need cells; delete takes none; create may seed
// cells. The home area is never created or deleted.
type Area struct {
	op             AreaOperation
	key            string
	pollutionClear bool
	rects          string // canonical JSON []AreaRect
}

// AreaRect is one inclusive rectangle of cells.
type AreaRect struct{ MinX, MinZ, MaxX, MaxZ int32 }

// AreaRects covers cells (no duplicates) with the fewest
// row-run rectangles a greedy vertical merge finds.
func AreaRects(cells []Cell) []AreaRect {
	cells = append([]Cell{}, cells...)
	sort.Slice(cells, func(i, j int) bool {
		return cells[i].Z < cells[j].Z || cells[i].Z == cells[j].Z && cells[i].X < cells[j].X
	})
	var out []AreaRect
	var open []int // out indexes of rects ending on the previous row
	prevZ := int32(-2)
	for i := 0; i < len(cells); {
		z := cells[i].Z
		var row []AreaRect
		for i < len(cells) && cells[i].Z == z {
			j := i
			for j+1 < len(cells) && cells[j+1].Z == z && cells[j+1].X == cells[j].X+1 {
				j++
			}
			row = append(row, AreaRect{cells[i].X, z, cells[j].X, z})
			i = j + 1
		}
		var next []int
		for _, r := range row {
			merged := false
			if z == prevZ+1 {
				for _, k := range open {
					if out[k].MinX == r.MinX && out[k].MaxX == r.MaxX {
						out[k].MaxZ = z
						next, merged = append(next, k), true
						break
					}
				}
			}
			if !merged {
				out = append(out, r)
				next = append(next, len(out)-1)
			}
		}
		open, prevZ = next, z
	}
	return out
}

// ExpandAreaRects lists the cells of rects row by row.
func ExpandAreaRects(rects []AreaRect) []Cell {
	var cells []Cell
	for _, r := range rects {
		for z := r.MinZ; z <= r.MaxZ; z++ {
			for x := r.MinX; x <= r.MaxX; x++ {
				cells = append(cells, Cell{x, z})
			}
		}
	}
	return cells
}

func NewArea(op AreaOperation, key string, cells []Cell) (Area, error) {
	return newArea(op, key, false, cells)
}

// NewPollutionClearArea edits the game's pollution-clear area, the
// cells the cleanup crew cleans: set_cells and clear_cells only, like home.
func NewPollutionClearArea(op AreaOperation, cells []Cell) (Area, error) {
	if op != AreaSetCells && op != AreaClearCells {
		return Area{}, errors.New("the pollution-clear area takes set_cells and clear_cells only")
	}
	return newArea(op, "", true, cells)
}

func newArea(op AreaOperation, key string, pollutionClear bool, cells []Cell) (Area, error) {
	switch op {
	case AreaCreate, AreaDelete:
		if !validID(key) {
			return Area{}, errors.New("area create and delete need a bot area key")
		}
	case AreaSetCells, AreaClearCells:
		if key != "" && !validID(key) || len(cells) == 0 {
			return Area{}, errors.New("area cell edits need cells")
		}
	default:
		return Area{}, errors.New("invalid area operation")
	}
	if op == AreaDelete && len(cells) > 0 {
		return Area{}, errors.New("area delete takes no cells")
	}
	rows := append([]Cell{}, cells...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].X < rows[j].X || rows[i].X == rows[j].X && rows[i].Z < rows[j].Z })
	for i, c := range rows {
		if c.X < 0 || c.Z < 0 || i > 0 && rows[i-1] == c {
			return Area{}, errors.New("invalid area cell")
		}
	}
	data, _ := json.Marshal(AreaRects(rows))
	if len(rows) == 0 {
		data = nil
	}
	return Area{op, key, pollutionClear, string(data)}, nil
}

func (a Area) Operation() AreaOperation { return a.op }

// Key is the bot area key; empty names the home or pollution-clear area.
func (a Area) Key() string { return a.key }
func (a Area) Home() bool  { return a.key == "" && !a.pollutionClear }

// PollutionClear names the game's pollution-clear area.
func (a Area) PollutionClear() bool { return a.pollutionClear }

// Rects is the canonical cover of the area edit; Cells expands it.
func (a Area) Rects() []AreaRect {
	var rects []AreaRect
	_ = json.Unmarshal([]byte(a.rects), &rects)
	return rects
}
func (a Area) Cells() []Cell { return ExpandAreaRects(a.Rects()) }

func NewAreaAction(id ActionID, a Area) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := newArea(a.op, a.key, a.pollutionClear, a.Cells())
	if err != nil || canonical != a {
		return Action{}, errors.New("invalid area")
	}
	return Action{id: id, kind: AreaAction, area: a}, nil
}

func (a Action) Area() (Area, bool) {
	return a.area, a.kind == AreaAction
}
