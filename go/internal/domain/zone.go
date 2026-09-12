package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

const ZoneCreateAction ActionKind = "zone_create"

type ZoneKind string

const GrowingZone ZoneKind = "growing"

// ZoneCreate owns one bounded connected footprint. Settings are closed variants:
// an explicitly sown crop.
type ZoneCreate struct {
	kind        ZoneKind
	crop, cells string
}

func NewZoneCreate(kind ZoneKind, crop string, cells []Cell) (ZoneCreate, error) {
	if kind != GrowingZone || !validID(crop) || len(cells) == 0 || len(cells) > 256 {
		return ZoneCreate{}, errors.New("invalid zone configuration")
	}
	rows := append([]Cell(nil), cells...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].X < rows[j].X || rows[i].X == rows[j].X && rows[i].Z < rows[j].Z })
	seen := map[Cell]bool{}
	for _, cell := range rows {
		if cell.X < 0 || cell.Z < 0 || seen[cell] {
			return ZoneCreate{}, errors.New("invalid zone cell")
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
		return ZoneCreate{}, errors.New("zone footprint disconnected")
	}
	data, _ := json.Marshal(rows)
	return ZoneCreate{kind, crop, string(data)}, nil
}
func (z ZoneCreate) Kind() ZoneKind { return z.kind }
func (z ZoneCreate) Crop() string   { return z.crop }
func (z ZoneCreate) Cells() []Cell {
	var cells []Cell
	_ = json.Unmarshal([]byte(z.cells), &cells)
	return cells
}
func (z ZoneCreate) Label() string { return "RimGovernor crops" }
func NewZoneCreateAction(id ActionID, z ZoneCreate) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewZoneCreate(z.kind, z.crop, z.Cells())
	if err != nil || canonical != z {
		return Action{}, errors.New("invalid zone value")
	}
	return Action{id: id, kind: ZoneCreateAction, zone: z}, nil
}
func (a Action) ZoneCreate() (ZoneCreate, bool) { return a.zone, a.kind == ZoneCreateAction }
