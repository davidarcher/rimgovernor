package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// rockEdgeMaps are 200x200 maps with mountain along an edge, a corner, a
// band across the middle and a pocket at the south: the shapes where the
// site scorer seeds beside rock and the perimeter ring and its killbox
// meet it (#1588).
var rockEdgeMaps = map[string]func(x, z int32) bool{
	"east mountain":    func(x, z int32) bool { return x >= 150 },
	"north-west slope": func(x, z int32) bool { return x+z < 120 },
	"middle band":      func(x, z int32) bool { return x >= 95 && x <= 105 && z < 150 },
	"south pocket":     func(x, z int32) bool { return z >= 160 && x > 40 && x < 160 },
}

func rockEdgeSurvey(rock func(x, z int32) bool) MapSurvey {
	return zoningSurvey(200, func(x, z int32) SurveyCell {
		if rock(x, z) {
			return SurveyCell{Rock: true, ThickRoof: true}
		}
		return SurveyCell{Walkable: true, Fertility: 0.7}
	})
}

// defenseCellsOver reads the survey as the census a defense layout sees: rock
// stands, blocks sight and walking, and the rest is open ground.
func defenseCellsOver(s MapSurvey, region Rectangle, dug map[domain.Cell]bool) []DefenseCell {
	byCell := map[domain.Cell]SurveyCell{}
	for _, c := range s.Cells {
		byCell[c.Cell] = c
	}
	var out []DefenseCell
	for _, c := range rectCells(region) {
		rock := byCell[c].Rock && !dug[c]
		cell := DefenseCell{Cell: c, Walkable: domain.Known(!rock), Passable: domain.Known(!rock),
			PlayerOwned: domain.Known(false), NaturalRock: domain.Known(rock), EdgeReachable: domain.Known(!rock),
			Door: domain.Known(false), CoverFill: domain.Known(0.0)}
		if rock {
			cell.CoverFill, cell.Edifice = domain.Known(1.0), "Granite"
		}
		out = append(out, cell)
	}
	return out
}

func siteCellsOver(cells []DefenseCell) []SiteCell {
	var out []SiteCell
	for _, c := range cells {
		site := SiteCell{Cell: c.Cell, Things: OccupantThings(false), Walkable: domain.Known(true), Roof: domain.Known("")}
		if positive(c.NaturalRock) {
			site.SetNaturalRock(true)
			site.Walkable, site.Roof = domain.Known(false), domain.Known("RoofRockThick")
		}
		out = append(out, site)
	}
	return out
}

// Over every rock-edge map the site scorer, the layout plan and the defense
// layout agree before anything reaches the game: the plan keeps its walls,
// gates and killbox off rock; the defense layout never stands on rock or
// reports a corridor it cannot walk; and rock across the killbox corridor
// (the #1588 shape, "corridor is not passable end to end") is either a dig
// the rock step lists or a refusal in Go, and once dug the corridor is
// walkable from its entry to the kill zone.
func TestRockEdgeMapsPlanWalkableAndBuildableLayouts(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	for name, rock := range rockEdgeMaps {
		t.Run(name, func(t *testing.T) {
			s := rockEdgeSurvey(rock)
			sited := SiteCore(LayoutPlan{Zones: Zone(s)}, s, 3, 1, TechTierCamp)
			if len(sited.AllRooms()) == 0 {
				t.Fatal("site scorer placed no rooms")
			}
			plan := PlanPerimeter(sited, s)
			if !plan.Valid() {
				t.Fatal("invalid layout plan")
			}
			if c, leak := perimeterLeak(plan, s); leak {
				t.Fatal("raiders reach the core at", c)
			}
			for _, kind := range []ReservationKind{ReservePerimeter, ReserveGate, ReserveKillbox} {
				for c := range reservedCells(plan, kind) {
					if rock(c.X, c.Z) {
						t.Fatal("plan reserves", kind, "on rock at", c)
					}
				}
			}
			killbox, region, home, ok := LayoutKillbox(plan, s.Bounds)
			if !ok {
				t.Fatal("plan has no killbox opening")
			}
			request := defenseFixture()
			request.Region, request.Bounds, request.Home, request.Entrances, request.Killbox = region, s.Bounds, home, nil, killbox
			request.Turret = DefenseTurretRequest{Definition: "Turret_MiniTurret", Conduit: "HiddenConduit"}
			request.Cells = defenseCellsOver(s, region, nil)

			planned, err := DefenseRockCells(request)
			if err != nil {
				t.Fatal(err)
			}
			dig := RockStep(planned, siteCellsOver(request.Cells)).Dig
			if layout, err := DefenseLayouts(request); err == nil {
				if len(dig) != 0 {
					t.Fatal("layout accepted rock on its corridor; dig lists", dig)
				}
				assertWalkableLayout(t, layout, request)
			} else if len(dig) == 0 {
				t.Fatal("layout refused with no rock to dig:", err)
			}
			dug := map[domain.Cell]bool{}
			for _, c := range dig {
				dug[c] = true
			}
			request.Cells = defenseCellsOver(s, region, dug)
			layout, err := DefenseLayouts(request)
			if err != nil {
				t.Fatal("layout waits after its dig:", err)
			}
			assertWalkableLayout(t, layout, request)
		})
	}
}

// The #1588 shape on a plan from the site scorer: rock laid across the first
// cells of the planned corridor lane makes the layout refuse in Go
// ("corridor is not passable end to end"), the rock step lists exactly that
// rock to dig, and the layout stands and walks once it is open.
func TestRockAcrossTheKillboxCorridorIsDugNotBuiltOn(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := rockEdgeSurvey(rockEdgeMaps["east mountain"])
	plan := PlanPerimeter(SiteCore(LayoutPlan{Zones: Zone(s)}, s, 3, 1, TechTierCamp), s)
	killbox, region, home, ok := LayoutKillbox(plan, s.Bounds)
	if !ok {
		t.Fatal("plan has no killbox opening")
	}
	request := defenseFixture()
	request.Region, request.Bounds, request.Home, request.Entrances, request.Killbox = region, s.Bounds, home, nil, killbox
	request.Turret = DefenseTurretRequest{Definition: "Turret_MiniTurret", Conduit: "HiddenConduit"}
	request.Cells = defenseCellsOver(s, region, nil)
	open, err := DefenseLayouts(request)
	if err != nil {
		t.Fatal(err)
	}
	across := map[domain.Cell]bool{}
	for _, c := range open.TrapLane[:3] {
		across[c] = true
	}
	for i, c := range request.Cells {
		if across[c.Cell] {
			request.Cells[i].Walkable, request.Cells[i].Passable, request.Cells[i].NaturalRock = domain.Known(false), domain.Known(false), domain.Known(true)
			request.Cells[i].Edifice = "Granite"
		}
	}
	if _, err = DefenseLayouts(request); err == nil || err.Error() != "corridor is not passable end to end" {
		t.Fatal("layout over rock on its corridor:", err)
	}
	planned, err := DefenseRockCells(request)
	if err != nil {
		t.Fatal(err)
	}
	dig := RockStep(planned, siteCellsOver(request.Cells)).Dig
	if len(dig) != len(across) {
		t.Fatalf("dig lists %v, want the %d rock cells on the lane", dig, len(across))
	}
	for _, c := range dig {
		if !across[c] {
			t.Fatal("dig lists a cell that is not the rock on the lane", c)
		}
	}
	request.Cells = defenseCellsOver(s, region, nil)
	layout, err := DefenseLayouts(request)
	if err != nil {
		t.Fatal("layout waits after the dig:", err)
	}
	assertWalkableLayout(t, layout, request)
}

// assertWalkableLayout checks a layout against the census it was made from:
// no building or lane cell on rock, and the lane walkable from its entry to
// the kill zone around the layout's own walls.
func assertWalkableLayout(t *testing.T, layout DefenseLayout, r DefenseRequest) {
	t.Helper()
	open := map[domain.Cell]bool{}
	for _, c := range r.Cells {
		open[c.Cell] = positive(c.Passable) && !positive(c.NaturalRock)
	}
	walled := map[domain.Cell]bool{}
	for _, tier := range layout.Tiers {
		for _, b := range tier.Buildings {
			if !open[b.Cell()] {
				t.Fatal("layout builds on rock or off the census at", b.Cell(), b.Definition())
			}
			if b.Definition() == r.Definitions.Wall {
				walled[b.Cell()] = true
			}
		}
	}
	reached := map[domain.Cell]bool{layout.Entry: true}
	queue := []domain.Cell{layout.Entry}
	for len(queue) > 0 {
		at := queue[0]
		queue = queue[1:]
		for _, d := range directions {
			next := addCell(at, d)
			if open[next] && !walled[next] && !reached[next] {
				reached[next] = true
				queue = append(queue, next)
			}
		}
	}
	for _, c := range layout.TrapLane {
		if !open[c] || walled[c] {
			t.Fatal("lane cell is not walkable", c)
		}
	}
	if !reached[layout.KillZone()] {
		t.Fatal("the kill zone cannot be walked to from", layout.Entry)
	}
}
