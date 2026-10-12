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

func TestSoilCostOrdersRichNormalBare(t *testing.T) {
	s := soilSurvey()
	g := newBuildGround(Zone(s), nil).withSoil(s)
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

// TestSoilCostBaselineFixture is soilCost on the recorded survey: every
// rich cell costs soilCostRich and rock costs nothing.
func TestSoilCostBaselineFixture(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	g := newBuildGround(Zone(s), nil).withSoil(s)
	rich, rock := 0, 0
	for _, c := range s.Cells {
		r := Rectangle{X: c.Cell.X, Z: c.Cell.Z, Width: 1, Height: 1}
		switch {
		case c.Rock:
			if g.soilCost(r) != soilCostOther {
				t.Fatal("rock cell costs", c.Cell, g.soilCost(r))
			}
			rock++
		case c.Fertility > zoneRichFertility:
			if g.soilCost(r) != soilCostRich {
				t.Fatal("rich cell costs", c.Cell, g.soilCost(r))
			}
			rich++
		}
	}
	whole := g.soilCost(Rectangle{Width: s.Bounds.Width, Height: s.Bounds.Height})
	if rich == 0 || rock == 0 || whole < rich*soilCostRich {
		t.Fatal("fixture soil cost", rich, rock, whole)
	}
}
