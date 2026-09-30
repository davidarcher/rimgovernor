package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func zoningSurvey(n int32, cell func(x, z int32) SurveyCell) MapSurvey {
	s := MapSurvey{Bounds: Bounds{Width: n, Height: n}}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			c := cell(x, z)
			c.Cell = domain.Cell{X: x, Z: z}
			s.Cells = append(s.Cells, c)
		}
	}
	return s
}

func zoningCells(zones []LayoutZone, kind ZoneKind) (map[domain.Cell]int, int) {
	out, n := map[domain.Cell]int{}, 0
	for _, z := range zones {
		if z.Kind != kind {
			continue
		}
		n++
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				out[domain.Cell{X: x, Z: r.Z}] = n
			}
		}
	}
	return out, n
}

func TestZoneRiverMap(t *testing.T) {
	// A river down x 45..49 across fertile soil.
	zones := Zone(zoningSurvey(100, func(x, z int32) SurveyCell {
		if x >= 45 && x < 50 {
			return SurveyCell{Footing: FootingNone}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	}))
	noGo, _ := zoningCells(zones, ZoneNoGo)
	core, _ := zoningCells(zones, ZoneCore)
	fields, patches := zoningCells(zones, ZoneField)
	for _, c := range []domain.Cell{{X: 47, Z: 50}, {X: 3, Z: 50}, {X: 50, Z: 95}} {
		if noGo[c] == 0 || core[c] != 0 || fields[c] != 0 {
			t.Fatal("no-go", c)
		}
	}
	if core[domain.Cell{X: 30, Z: 30}] == 0 || fields[domain.Cell{X: 30, Z: 30}] == 0 {
		t.Fatal("soil is core candidate and field")
	}
	// One field per river bank: no lattice gaps, and no field crosses the river.
	if patches != 2 || fields[domain.Cell{X: 11, Z: 20}] == 0 || fields[domain.Cell{X: 12, Z: 20}] == 0 {
		t.Fatal("one field per bank", patches)
	}
	if fields[domain.Cell{X: 30, Z: 30}] == fields[domain.Cell{X: 60, Z: 30}] {
		t.Fatal("field crosses the river")
	}
}

func TestZoneMountainMap(t *testing.T) {
	// Rock east of x 60, an ore seam at z 50, gravel west.
	zones := Zone(zoningSurvey(100, func(x, z int32) SurveyCell {
		if x >= 60 {
			return SurveyCell{Rock: true, ThickRoof: true, Ore: z == 50}
		}
		return SurveyCell{Walkable: true, Fertility: 0.7, Tree: x < 20}
	}))
	var mining []ZoneKind
	for _, z := range zones {
		if z.Kind == ZoneMining {
			mining = append(mining, z.Kind)
		}
	}
	first := -1
	for i, z := range zones {
		if z.Kind == ZoneMining {
			first = i
			break
		}
	}
	if len(mining) != 2 || first < 0 || zones[first].Runs[0].Z != 50 {
		t.Fatal("ore first", mining)
	}
	core, _ := zoningCells(zones, ZoneCore)
	pasture, _ := zoningCells(zones, ZonePasture)
	wood, _ := zoningCells(zones, ZoneWood)
	_, fields := zoningCells(zones, ZoneField)
	if core[domain.Cell{X: 70, Z: 30}] == 0 || core[domain.Cell{X: 30, Z: 30}] == 0 {
		t.Fatal("rock and ground are core candidates")
	}
	if fields != 0 || pasture[domain.Cell{X: 30, Z: 30}] == 0 || pasture[domain.Cell{X: 70, Z: 30}] != 0 {
		t.Fatal("gravel is pasture, not field")
	}
	if wood[domain.Cell{X: 15, Z: 30}] == 0 || wood[domain.Cell{X: 30, Z: 30}] != 0 {
		t.Fatal("wood")
	}
}

func TestZoneOpenPlains(t *testing.T) {
	s := zoningSurvey(60, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1.4} })
	s.Cells = s.Cells[:len(s.Cells)-60] // the top row is fogged
	zones := Zone(s)
	core, _ := zoningCells(zones, ZoneCore)
	noGo, _ := zoningCells(zones, ZoneNoGo)
	fields, patches := zoningCells(zones, ZoneField)
	if len(core) != 40*40 || noGo[domain.Cell{X: 5, Z: 59}] != 0 || noGo[domain.Cell{X: 5, Z: 58}] == 0 {
		t.Fatal(len(core))
	}
	if _, n := zoningCells(zones, ZoneMining); n != 0 {
		t.Fatal("mining on plains")
	}
	if patches != 1 || len(fields) != 40*40 || fields[domain.Cell{X: 12, Z: 12}] == 0 {
		t.Fatal("one field", patches, len(fields))
	}
	if !(LayoutPlan{Rooms: []LayoutRoom{{Role: ModuleKitchen, Interior: Rectangle{Width: 1, Height: 1}, DoorRot: domain.North}}, Zones: zones}).Valid() {
		t.Fatal("zones persist")
	}
}
