package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// AreaPlantCutAction orders every non-crop plant on cells cut: an
// AreaPlantCutIntent on Actions/Apply. Wild plants get CutPlant, harvestable
// trees chop-wood; growing-zone plants and sown crops are never touched.
// Native validates the cells live; a cell with nothing to cut is a no-op.
// Applied means designated; the plant cut census decides when pawns finished.
const AreaPlantCutAction ActionKind = "area_plant_cut"

// MaxAreaPlantCutCells bounds one sweep, as the census bounds one read.
const MaxAreaPlantCutCells = 1024

// AreaPlantCut is an immutable, comparable value: canonical cells (sorted,
// deduplicated), at least one.
type AreaPlantCut struct{ cells string }

func NewAreaPlantCut(cells []Cell) (AreaPlantCut, error) {
	if len(cells) == 0 || len(cells) > MaxAreaPlantCutCells {
		return AreaPlantCut{}, errors.New("area plant cut needs 1..1024 cells")
	}
	rows := append([]Cell{}, cells...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].X < rows[j].X || rows[i].X == rows[j].X && rows[i].Z < rows[j].Z })
	for i, c := range rows {
		if c.X < 0 || c.Z < 0 || i > 0 && rows[i-1] == c {
			return AreaPlantCut{}, errors.New("invalid area plant cut cell")
		}
	}
	data, _ := json.Marshal(rows)
	return AreaPlantCut{string(data)}, nil
}

func (r AreaPlantCut) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(r.cells), &cells)
	return cells
}

func NewAreaPlantCutAction(id ActionID, r AreaPlantCut) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewAreaPlantCut(r.Cells())
	if err != nil || canonical != r {
		return Action{}, errors.New("invalid area plant cut")
	}
	return Action{id: id, kind: AreaPlantCutAction, areaPlantCut: r}, nil
}

func (a Action) AreaPlantCut() (AreaPlantCut, bool) {
	return a.areaPlantCut, a.kind == AreaPlantCutAction
}
