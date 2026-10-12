package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The wall lamp (#2948): the standing lamp's rival, priced in the same watts.

func TestWallLampPriceAndReachComeFromTheDefs(t *testing.T) {
	p := testLightPrice()
	if want := 30 + BuildW(DefaultSiteTypeWeights(), 28.5, 1.9); math.Abs(p.WallLampW-want) > 1e-6 {
		t.Errorf("wall lamp %v W, want %v", p.WallLampW, want)
	}
	if p.WallLampW >= p.LampW {
		t.Errorf("a wall lamp (15 steel) should cost less than a standing one (20): %v vs %v W", p.WallLampW, p.LampW)
	}
	if want := lampReach * 11 / 12; math.Abs(p.WallReach-want) > 1e-9 {
		t.Errorf("wall reach %v, want the standing reach scaled by glow radius %v", p.WallReach, want)
	}
	// Without the wall lamp's def, or a radius, the option is absent and the
	// price is still valid.
	for name, model := range map[string]LightModel{
		"no wall lamp":        {Standing: LampModel{30, 38, 12}},
		"no wall radius":      {Standing: LampModel{30, 38, 12}, Wall: LampModel{PowerW: 30, Build: 28.5}},
		"no standing radius":  {Standing: LampModel{PowerW: 30, Build: 38}, Wall: LampModel{30, 28.5, 11}},
		"wall lamp unpowered": {Standing: LampModel{30, 38, 12}, Wall: LampModel{Build: 28.5, Radius: 11}},
	} {
		q := NewLightPrice(LightPriceInput{Model: model, Climate: testClimate(), SteelValue: 1.9, UpkeepPerDay: 1.6, UnitNutrition: 0.055})
		if !q.Valid() || q.WallLampW != 0 || q.WallReach != 0 {
			t.Errorf("%s: %+v", name, q)
		}
	}
}

func lampsAt(p LightPrice, spot domain.Cell, notWall map[domain.Cell]bool) []lampCand {
	return append(p.standingCands([]domain.Cell{spot}), wallLampCands([]domain.Cell{spot}, notWall, p)...)
}

// Both lamps can stand on the cell; the cheaper cover is the wall lamp where its
// shorter reach still lights the dark, the standing lamp where only it reaches.
func TestPlanLampsPicksTheCheaperCover(t *testing.T) {
	p := testLightPrice()
	spot := domain.Cell{X: 0, Z: 0}
	w := p.WorkCellW()
	for _, tc := range []struct {
		name string
		dark domain.Cell
		def  string
	}{
		{"within both reaches", domain.Cell{X: 4, Z: 0}, lightTable.WallLampDef},
		{"only the standing lamp reaches", domain.Cell{X: 6, Z: 0}, lightTable.LampDef},
	} {
		got, left := planLamps([]DarkCell{{Cell: tc.dark, W: w}}, lampsAt(p, spot, nil))
		if len(got) != 1 || got[0].Def != tc.def || left != 0 {
			t.Errorf("%s: %+v, left %v; want one %s", tc.name, got, left, tc.def)
		}
	}
	// Out of both reaches nothing pays.
	if got, left := planLamps([]DarkCell{{Cell: domain.Cell{X: 7, Z: 0}, W: w}}, lampsAt(p, spot, nil)); len(got) != 0 || left != w {
		t.Errorf("out of reach: %+v, left %v", got, left)
	}
	// A wall lamp dearer than the dark it lights, with no standing lamp, is not
	// placed.
	dear := p
	dear.WallLampW = w * 2
	cands := wallLampCands([]domain.Cell{spot}, nil, dear)
	if got, _ := planLamps([]DarkCell{{Cell: domain.Cell{X: 4, Z: 0}, W: w}}, cands); len(got) != 0 {
		t.Errorf("a wall lamp dearer than the dark was placed: %+v", got)
	}
}

func TestWallLampsHangOnPlainWallOnly(t *testing.T) {
	p := testLightPrice()
	spot := domain.Cell{X: 5, Z: 5}
	// Every neighbour is floor, a door or a link: no wall lamp.
	notWall := map[domain.Cell]bool{}
	for _, d := range directions {
		notWall[addCell(spot, d)] = true
	}
	if got := wallLampCands([]domain.Cell{spot}, notWall, p); len(got) != 0 {
		t.Fatalf("wall lamp with no wall: %+v", got)
	}
	// A door to the north and floor to the east leave the south wall.
	notWall = map[domain.Cell]bool{addCell(spot, directions[0]): true, addCell(spot, directions[1]): true}
	got := wallLampCands([]domain.Cell{spot}, notWall, p)
	if len(got) != 1 || got[0].Facing != domain.South || got[0].Def != lightTable.WallLampDef {
		t.Fatalf("wall lamp %+v, want one facing the south wall", got)
	}
}

// A wall cell a vent holds (or a door, a link) prices the wall lamp out of that
// spot and the standing lamp answers; the dark is still lit, nothing is refused.
func TestContestedWallCellFallsBackToTheStandingLamp(t *testing.T) {
	p := testLightPrice()
	spot := domain.Cell{X: 2, Z: 2}
	dark := []DarkCell{{Cell: spot, W: p.WorkCellW()}}
	got, _ := planLamps(dark, lampsAt(p, spot, nil))
	if len(got) != 1 || got[0].Def != lightTable.WallLampDef {
		t.Fatalf("free wall: %+v", got)
	}
	held := map[domain.Cell]bool{}
	for _, d := range directions {
		held[addCell(spot, d)] = true
	}
	got, left := planLamps(dark, lampsAt(p, spot, held))
	if len(got) != 1 || got[0].Def != lightTable.LampDef || left != 0 {
		t.Fatalf("held wall: %+v, left %v; want the standing lamp", got, left)
	}
}

func TestTilerWallLampsHangOnWallsBesideTheirFloorCell(t *testing.T) {
	in := lampTileInput(t)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	notWall := (&tiler{out: res}).notPlainWall()
	walls := 0
	for id, lamps := range res.Lamps {
		for _, l := range lamps {
			if l.Def == lightTable.LampDef {
				if l.Facing != "" {
					t.Errorf("room %d standing lamp faces %q", id, l.Facing)
				}
				continue
			}
			walls++
			if l.Def != lightTable.WallLampDef {
				t.Errorf("room %d lamp %+v is neither lamp def", id, l)
			}
			if support := addCell(l.Cell, directionOf(l.Facing)); notWall[support] {
				t.Errorf("room %d wall lamp %+v hangs on %v, which is floor, a door or a link", id, l, support)
			}
		}
	}
	if walls == 0 {
		t.Errorf("no room took a wall lamp, which is cheaper than a standing one: %+v", res.Lamps)
	}
}

// The vents the tiler placed (#2915) hold their wall cells: no wall lamp may
// count them as a plain wall.
func TestPlacedVentsHoldTheirWallCell(t *testing.T) {
	res := ventTiling(t, seaIce, ventYes, furnaceHeat)
	vents := ventsOf(res)
	if len(vents) == 0 {
		t.Fatal("no vent was placed to hold a wall cell")
	}
	notWall := (&tiler{out: res}).notPlainWall()
	for _, v := range vents {
		for _, c := range v.Footprint.Cells() {
			if !notWall[c] {
				t.Errorf("vent cell %v counted as a plain wall", c)
			}
		}
	}
}

func TestTilerWithoutAWallLampPlacesStandingLampsOnly(t *testing.T) {
	in := lampTileInput(t)
	in.Light.WallLampW, in.Light.WallReach = 0, 0
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Lamps) == 0 {
		t.Fatal("no lamps")
	}
	for id, lamps := range res.Lamps {
		for _, l := range lamps {
			if l.Def != lightTable.LampDef {
				t.Errorf("room %d lamp %+v is not a standing lamp", id, l)
			}
		}
	}
}
