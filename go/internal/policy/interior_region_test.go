package policy

import (
	"fmt"
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// regionFamilies are the roles the region placer furnishes, with the module
// each is sized as. The parity tests hold every one to its template's capacity,
// slot names and gated objective terms over the sizes in use and two irregular
// shapes (#2956).
// Floor is the (width, depth) of the shape the irregular-floor test cuts.
var regionFamilies = []struct {
	Role    RoomRole
	Planned PlannedRole
	Floor   [2]int32
}{
	{RoomRoleWorkshop, PlannedWorkshop, [2]int32{7, 5}}, {RoomRoleLaboratory, PlannedLab, [2]int32{11, 6}}, {RoomRoleKitchen, PlannedKitchen, [2]int32{6, 5}},
	{RoomRoleTomb, PlannedTomb, [2]int32{5, 5}}, {RoomRolePrisonCell, PlannedPrison, [2]int32{5, 5}}, {RoomRoleHospital, PlannedHospital, [2]int32{7, 5}},
}

func regionFamilyFixtures() []objectiveFixture {
	var out []objectiveFixture
	for _, f := range objectiveFixtures() {
		if _, ok := regionRoles[f.Role]; ok {
			out = append(out, f)
		}
	}
	return out
}

// irregularRoom is the fixture's room with its floor cut: an L (the far corner
// away) or a notch (a 1x1 bite out of the back wall's middle). The door stays on
// the south wall's middle cell and the region stays one piece.
func irregularRoom(f objectiveFixture, kind string) (InteriorRoom, Region, []domain.Cell) {
	room, _, cands := objectiveRoom(f)
	r := room.Interior
	var cut []domain.Cell
	switch kind {
	case "L":
		for x := r.X + r.Width - max(r.Width/3, 1); x < r.X+r.Width; x++ {
			for z := r.Z + r.Height - max(r.Height/3, 1); z < r.Z+r.Height; z++ {
				cut = append(cut, domain.Cell{X: x, Z: z})
			}
		}
	case "notch":
		cut = append(cut, domain.Cell{X: r.X + r.Width/2, Z: r.Z + r.Height - 1})
	}
	var cells []domain.Cell
	for _, c := range rectCells(r) {
		if !slices.Contains(cut, c) {
			cells = append(cells, c)
		}
	}
	region := RegionOf(cells)
	room.Region = region
	room.Interior = region.Bounds()
	return room, region, cands
}

func defCounts(pieces []InteriorPiece) map[string]int {
	out := map[string]int{}
	for _, p := range pieces {
		out[p.Def]++
	}
	return out
}

// searchedWith is the region plan of the room searched with the station
// graph's flows, which a live caller does not pass yet.
func searchedWith(t *testing.T, f objectiveFixture, room InteriorRoom, region Region, cands []domain.Cell) (InteriorSearchResult, RegionPlacement, bool) {
	req, ok := regionRequest(room, InteriorPieceDef{})
	if !ok {
		return InteriorSearchResult{}, req, false
	}
	res, err := SearchInterior(req, InteriorSearch{Candidates: cands, Shapes: testShapes, Dining: testDining, Flows: objectiveFlows(t, f.Role), Seed: regionSeed(room), Iterations: regionParityIterations})
	if err != nil {
		return InteriorSearchResult{}, req, false
	}
	return res, req, true
}

// Per family, over the sizes in use: the region plan holds at least the
// template's count of every definition, carries every slot name the template
// does (the reconcilers and runtime consumers read them), is feasible, and its
// searched layout is no worse than the template's on any gated term. Free floor
// is compared only when the region plan holds no more pieces than the template:
// more capacity is what #2956 asks for, and costs floor.
func TestRegionInteriorMatchesTemplates(t *testing.T) {
	var table strings.Builder
	for _, f := range regionFamilyFixtures() {
		key := fmt.Sprintf("%s %s %dx%d", f.Role, f.Name, f.W, f.D)
		room, region, cands := objectiveRoom(f)
		tpl, tok := PlanInterior(room, InteriorPieceDef{})
		reg, rok := PlanRegionInterior(room, InteriorPieceDef{})
		if tok && !rok {
			t.Errorf("%s: the template plans, the region placer does not", key)
			continue
		}
		if !tok {
			continue
		}
		have, want := defCounts(reg.Pieces), defCounts(tpl.Pieces)
		for def, n := range want {
			if have[def] < n {
				t.Errorf("%s: %s: region plan holds %d, template %d", key, def, have[def], n)
			}
		}
		names := map[string]string{}
		for _, p := range reg.Pieces {
			names[p.Slot] = p.Def
		}
		for _, p := range tpl.Pieces {
			if names[p.Slot] != p.Def {
				t.Errorf("%s: template slot %s (%s) is missing from the region plan %v", key, p.Slot, p.Def, names)
			}
		}
		res, req, ok := searchedWith(t, f, room, region, cands)
		if !ok {
			t.Errorf("%s: no searched region plan", key)
			continue
		}
		in := InteriorObjective{Region: region, Candidates: cands, Doors: room.Doors, Pieces: tpl.Pieces, Required: want, Shapes: testShapes, Dining: testDining, Flows: objectiveFlows(t, f.Role)}
		ts := ScoreInterior(in)
		if !res.Score.Feasible() {
			t.Errorf("%s: region plan breaks hard constraints: %v", key, res.Score.Violations)
		}
		worse := res.Score.WorseTerms(ts)
		if len(res.Stood()) > len(tpl.Pieces) {
			worse = slices.DeleteFunc(worse, func(term string) bool { return term == "free" })
		}
		if len(worse) > 0 {
			t.Errorf("%s: region plan worse than the template on %v:\n region   %s\n template %s", key, worse, objectiveCells(&res.Score), objectiveCells(&ts))
		}
		_ = req
		fmt.Fprintf(&table, "%-12s %-9s %2dx%d  template: %s | region: %s\n", f.Role, f.Name, f.W, f.D, objectiveCells(&ts), objectiveCells(&res.Score))
	}
	t.Log("\n" + table.String())
}

// Two irregular shapes per role: the plan stays on the floor, keeps the doors
// and every cell reachable, holds the role's minimum, and is no worse than the
// greedy layout on any gated term.
func TestRegionInteriorFitsIrregularFloors(t *testing.T) {
	for _, fam := range regionFamilies {
		fixture := objectiveFixture{Role: fam.Role, Name: "core", W: fam.Floor[0], D: fam.Floor[1]}
		for _, kind := range []string{"L", "notch"} {
			room, region, cands := irregularRoom(fixture, kind)
			key := fmt.Sprintf("%s %s", fam.Role, kind)
			plan, ok := PlanRegionInterior(room, InteriorPieceDef{})
			if !ok {
				t.Errorf("%s: not furnished", key)
				continue
			}
			greedy, ok := planRegion(room, InteriorPieceDef{}, false)
			if !ok {
				t.Errorf("%s: the greedy placer does not furnish it", key)
				continue
			}
			score := func(ps []InteriorPiece) InteriorScore {
				return ScoreInterior(InteriorObjective{Region: region, Candidates: cands, Doors: room.Doors, Pieces: ps, Shapes: testShapes, Dining: testDining, Flows: objectiveFlows(t, fam.Role)})
			}
			s := score(plan.Pieces)
			if !s.Feasible() {
				t.Errorf("%s: infeasible: %v", key, s.Violations)
			}
			if len(plan.Pieces) != len(greedy.Pieces) {
				t.Errorf("%s: search holds %d pieces, greedy %d", key, len(plan.Pieces), len(greedy.Pieces))
			}
			again, _ := PlanRegionInterior(room, InteriorPieceDef{})
			if !reflect.DeepEqual(again.Pieces, plan.Pieces) {
				t.Errorf("%s: two plans of the same room differ", key)
			}
			res, _, ok := searchedWith(t, fixture, room, region, cands)
			if !ok {
				t.Errorf("%s: no searched plan", key)
				continue
			}
			if worse := res.Score.WorseTerms(score(greedy.Pieces)); len(worse) > 0 {
				t.Errorf("%s: searched layout worse than greedy on %v", key, worse)
			}
		}
	}
}

// A room the template cannot plan stays unplanned: no door, a floor too shallow
// for a bench row and its worker row.
func TestRegionInteriorRefusesWhatTheTemplateRefuses(t *testing.T) {
	shallow := InteriorRoom{Shapes: testShapes, Role: RoomRoleWorkshop, Interior: Rectangle{X: 0, Z: 0, Width: 7, Height: 2}, Doors: []domain.Cell{{X: 1, Z: -1}}}
	if _, ok := PlanRegionInterior(shallow, InteriorPieceDef{}); ok {
		t.Error("a two-deep room planned a workshop")
	}
	room, _, _ := objectiveRoom(objectiveFixture{Role: RoomRoleWorkshop, W: 7, D: 5})
	room.Doors = nil
	if _, ok := PlanRegionInterior(room, InteriorPieceDef{}); ok {
		t.Error("a room with no door was planned")
	}
}

// regionParityIterations is the search budget the parity tests give a room with
// the station graph flows. At the default budget a flow-weighted workshop 7x5
// leaves its pair walk at the greedy layout for some seeds (about one in eight);
// four times the default reached the template or better for every seed tried.
const regionParityIterations = 4 * DefaultInteriorSearchIterations
