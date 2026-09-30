package policy

import "testing"

// soilSurvey: rich soil (x < 20), plain soil (x 20..39), bare sand
// (x 40..54), rock from x 55.
func soilSurvey() MapSurvey {
	return zoningSurvey(70, func(x, z int32) SurveyCell {
		switch {
		case x < 20:
			return SurveyCell{Walkable: true, Fertility: 1.4}
		case x < 40:
			return SurveyCell{Walkable: true, Fertility: 1}
		case x < 55:
			return SurveyCell{Walkable: true, Fertility: 0}
		}
		return SurveyCell{Rock: true}
	})
}

func TestFieldRichCells(t *testing.T) {
	s := soilSurvey()
	rich := FieldRichCells(s, Zone(s))
	// One field over x < 40 inside the edge margin; its rich part is x < 20.
	e := int32(LayoutEdgeMargin)
	want := int((20 - e) * (70 - 2*e))
	if len(rich) != 1 || rich[0] != want {
		t.Fatal("rich cells per field", rich, want)
	}
}

func TestSoilCostOrdersRichNormalBare(t *testing.T) {
	s := soilSurvey()
	g := newCoreGrid(Zone(s), nil).withSoil(s)
	rich := g.soilCost(Rectangle{X: 10, Z: 30, Width: 5, Height: 5})
	normal := g.soilCost(Rectangle{X: 30, Z: 30, Width: 5, Height: 5})
	bare := g.soilCost(Rectangle{X: 45, Z: 30, Width: 5, Height: 5})
	rock := g.soilCost(Rectangle{X: 60, Z: 30, Width: 5, Height: 5})
	if !(rich > normal && normal > bare) || rock != bare {
		t.Fatal("soil cost order", rich, normal, bare, rock)
	}
	if g.soilCost(Rectangle{X: 18, Z: 30, Width: 4, Height: 1}) != 2*soilCostRich+2*soilCostNormal {
		t.Fatal("mixed rectangle sums per cell")
	}
}
