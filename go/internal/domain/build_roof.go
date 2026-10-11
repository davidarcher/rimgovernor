package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// BuildRoofAction designates vanilla BuildRoof (the Build Roof area) over cells: a
// BuildRoofIntent on Actions/Apply, the mirror of RemoveRoofAction. The planner
// issues it over the shelter porch with the first battery placed there.
// Applied means designated; the roof read decides when pawns finished.
const BuildRoofAction ActionKind = "build_roof"

// BuildRoof is an immutable, comparable value: canonical cells (sorted,
// deduplicated), at least one.
type BuildRoof struct{ cells string }

func NewBuildRoof(cells []Cell) (BuildRoof, error) {
	if len(cells) == 0 {
		return BuildRoof{}, errors.New("build roof needs cells")
	}
	rows := append([]Cell{}, cells...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].X < rows[j].X || rows[i].X == rows[j].X && rows[i].Z < rows[j].Z })
	for i, c := range rows {
		if c.X < 0 || c.Z < 0 || i > 0 && rows[i-1] == c {
			return BuildRoof{}, errors.New("invalid build roof cell")
		}
	}
	data, _ := json.Marshal(rows)
	return BuildRoof{string(data)}, nil
}

func (r BuildRoof) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(r.cells), &cells)
	return cells
}

func NewBuildRoofAction(id ActionID, r BuildRoof) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewBuildRoof(r.Cells())
	if err != nil || canonical != r {
		return Action{}, errors.New("invalid build roof")
	}
	return Action{id: id, kind: BuildRoofAction, buildRoof: r}, nil
}

func (a Action) BuildRoof() (BuildRoof, bool) {
	return a.buildRoof, a.kind == BuildRoofAction
}
