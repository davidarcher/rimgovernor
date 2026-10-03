package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// AreaAction creates, edits or deletes one bot-owned allowed area, or edits
// the home area (#1321): an AreaIntent on Actions/Apply. A bot-owned area is
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
	cells          string
}

func NewArea(op AreaOperation, key string, cells []Cell) (Area, error) {
	return newArea(op, key, false, cells)
}

// NewPollutionClearArea edits the game's pollution-clear area (#1683), the
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
	data, _ := json.Marshal(rows)
	return Area{op, key, pollutionClear, string(data)}, nil
}

func (a Area) Operation() AreaOperation { return a.op }

// Key is the bot area key; empty names the home or pollution-clear area.
func (a Area) Key() string { return a.key }
func (a Area) Home() bool  { return a.key == "" && !a.pollutionClear }

// PollutionClear names the game's pollution-clear area.
func (a Area) PollutionClear() bool { return a.pollutionClear }
func (a Area) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(a.cells), &cells)
	return cells
}

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
