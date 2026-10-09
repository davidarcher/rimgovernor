package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// RemoveRoofAction designates vanilla RemoveRoof over cells: a
// RemoveRoofIntent on Actions/Apply. Clearance issues it over an enclosed
// room's roofed cells before deconstructing the walls holding that roof.
// Applied means designated; the roof read decides when pawns finished.
const RemoveRoofAction ActionKind = "remove_roof"

// RemoveRoof is an immutable, comparable value: canonical cells (sorted,
// deduplicated), at least one.
type RemoveRoof struct{ cells string }

func NewRemoveRoof(cells []Cell) (RemoveRoof, error) {
	if len(cells) == 0 {
		return RemoveRoof{}, errors.New("remove roof needs cells")
	}
	rows := append([]Cell{}, cells...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].X < rows[j].X || rows[i].X == rows[j].X && rows[i].Z < rows[j].Z })
	for i, c := range rows {
		if c.X < 0 || c.Z < 0 || i > 0 && rows[i-1] == c {
			return RemoveRoof{}, errors.New("invalid remove roof cell")
		}
	}
	data, _ := json.Marshal(rows)
	return RemoveRoof{string(data)}, nil
}

func (r RemoveRoof) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(r.cells), &cells)
	return cells
}

func NewRemoveRoofAction(id ActionID, r RemoveRoof) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewRemoveRoof(r.Cells())
	if err != nil || canonical != r {
		return Action{}, errors.New("invalid remove roof")
	}
	return Action{id: id, kind: RemoveRoofAction, removeRoof: r}, nil
}

func (a Action) RemoveRoof() (RemoveRoof, bool) {
	return a.removeRoof, a.kind == RemoveRoofAction
}
