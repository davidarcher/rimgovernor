package policy

import (
	"fmt"
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// regionFixture is a role at a size with the piece it is asked to furnish
// around and the pieces its caller requires.
type regionFixture struct {
	objectiveFixture
	Piece    InteriorPieceDef
	Required []RequiredPiece
	// Occupants and Coolers are a shelter's sleepers and passive coolers; its
	// campfires are a cold map's.
	Occupants, Coolers int
}

func (f regionFixture) room() (InteriorRoom, Region, []domain.Cell) {
	room, region, cands := objectiveRoom(f.objectiveFixture)
	room.Required = f.Required
	if f.Role == RoomRoleShelter {
		room.Occupants, room.Coolers = f.Occupants, f.Coolers
		room.Campfires, room.Capabilities = ShelterCampfires(true), CapabilitiesOf(PlannedShelter)
	}
	return room, region, cands
}

func shelterFixture(name string, w, d int32, occupants, coolers int) regionFixture {
	f := sizedFixture(RoomRoleShelter, name, w, d, InteriorPieceDef{})
	f.Occupants, f.Coolers = occupants, coolers
	return f
}

func sizedFixture(role RoomRole, name string, w, d int32, piece InteriorPieceDef, required ...RequiredPiece) regionFixture {
	return regionFixture{objectiveFixture: objectiveFixture{Role: role, Name: name, W: w, D: d}, Piece: piece, Required: required}
}

var (
	testThrone   = InteriorPieceDef{Def: "Throne", Size: domain.Cell{X: 1, Z: 1}}
	testBrazier  = InteriorPieceDef{Def: "Brazier", Size: domain.Cell{X: 1, Z: 1}}
	testCrib     = InteriorPieceDef{Def: "Crib", Size: domain.Cell{X: 1, Z: 1}}
	testToyBox   = InteriorPieceDef{Def: "ToyBox", Size: domain.Cell{X: 2, Z: 1}}
	testSchoolDk = InteriorPieceDef{Def: "SchoolDesk", Size: domain.Cell{X: 1, Z: 2}}
)

// regionSizedFixtures are the families whose sizes are not the core table's: a
// bedroom per wing tier and the sizes the sleeping tests use, suites, throne
// rooms with and without the throne and a title's pieces, and the child rooms
// for the pieces they hold.
func regionSizedFixtures() []regionFixture {
	var out []regionFixture
	for _, s := range [][2]int32{{3, 4}, {4, 4}, {4, 5}, {5, 4}, {6, 5}, {7, 6}} {
		out = append(out, sizedFixture(RoomRoleBedroom, "bed", s[0], s[1], InteriorPieceDef{}))
	}
	out = append(out, sizedFixture(RoomRoleBedroom, "double", 6, 5, testShapes.Defs["DoubleBed"]))
	for _, s := range [][2]int32{{5, 6}, {7, 8}} {
		out = append(out, sizedFixture(RoomRoleSuite, "suite", s[0], s[1], InteriorPieceDef{}))
	}
	// The shelter sizes the layout plan picks for n colonists, a shelter filled
	// with every bunk that fits, and a hot map's cooler.
	for _, n := range []int{1, 2, 3, 4, 6, 8, 12} {
		s := ShelterSizes(n, ShelterCampfires(true), 0)[0]
		out = append(out, shelterFixture(fmt.Sprintf("%d sleepers", n), s[0], s[1], n, 0))
	}
	out = append(out, shelterFixture("fill", 7, 5, 0, 0), shelterFixture("cooler", 6, 5, 2, 1))
	// Deep enough for the pin's lane and a table beside it, then for several.
	for _, role := range []RoomRole{RoomRoleDiningRoom, RoomRoleRecRoom} {
		for _, s := range [][2]int32{{9, 11}, {9, 16}, {12, 12}} {
			out = append(out, sizedFixture(role, "deep", s[0], s[1], InteriorPieceDef{}))
		}
	}
	brazier := func(i int) RequiredPiece {
		return RequiredPiece{Slot: fmt.Sprintf("brazier.%d", i), Piece: testBrazier}
	}
	for _, s := range [][2]int32{{5, 5}, {6, 5}, {7, 6}} {
		out = append(out, sizedFixture(RoomRoleThroneRoom, "throne", s[0], s[1], testThrone, brazier(0), brazier(1)),
			sizedFixture(RoomRoleThroneRoom, "furniture", s[0], s[1], InteriorPieceDef{}))
	}
	for _, c := range []struct {
		role  RoomRole
		piece InteriorPieceDef
	}{{RoomRoleNursery, testCrib}, {RoomRolePlayroom, testToyBox}, {RoomRoleClassroom, testSchoolDk}, {RoomRoleWorshipRoom, testCrib}} {
		for _, s := range [][2]int32{{4, 4}, {5, 4}, {6, 5}} {
			out = append(out, sizedFixture(c.role, c.piece.Def, s[0], s[1], c.piece))
		}
	}
	return out
}

// regionFamilyFixtures are the sizes in use of every role the region placer
// furnishes: the core and variant sizes of the objective fixtures, and the
// sized families.
func regionFamilyFixtures() []regionFixture {
	sized := regionSizedFixtures()
	var out []regionFixture
	for _, f := range objectiveFixtures() {
		if _, ok := regionRoles[f.Role]; ok && !slices.ContainsFunc(sized, func(s regionFixture) bool { return s.Role == f.Role }) {
			out = append(out, regionFixture{objectiveFixture: f})
		}
	}
	return append(out, sized...)
}

// regionFamilies are the roles the region placer furnishes, each at its core
// size (or one fixture of the sized families). The parity tests hold every one
// to its template's capacity, slot names and gated objective terms over the
// sizes in use and two irregular shapes (#2956).
func regionFamilies() []regionFixture {
	var out []regionFixture
	for _, c := range []struct {
		role RoomRole
		w, d int32
	}{
		{RoomRoleWorkshop, 7, 5}, {RoomRoleLaboratory, 11, 6}, {RoomRoleKitchen, 6, 5},
		{RoomRoleTomb, 5, 5}, {RoomRolePrisonCell, 5, 5}, {RoomRoleHospital, 7, 5},
		{RoomRoleDiningRoom, 9, 7}, {RoomRoleRecRoom, 9, 7},
	} {
		out = append(out, sizedFixture(c.role, "core", c.w, c.d, InteriorPieceDef{}))
	}
	out = append(out, shelterFixture("core", 7, 5, 3, 0))
	seen := map[RoomRole]bool{}
	for _, f := range regionSizedFixtures() {
		if !seen[f.Role] && f.W >= 5 && f.D >= 5 {
			seen[f.Role] = true
			out = append(out, f)
		}
	}
	return out
}

// irregularRoom is the fixture's room with its floor cut: an L (the far corner
// away) or a notch (a 1x1 bite out of the back wall's middle). The door stays on
// the south wall's middle cell and the region stays one piece.
func irregularRoom(f regionFixture, kind string) (InteriorRoom, Region, []domain.Cell) {
	room, _, cands := f.room()
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
func searchedWith(t *testing.T, f regionFixture, room InteriorRoom, region Region, cands []domain.Cell) (InteriorSearchResult, RegionPlacement, bool) {
	req, ok := regionRequest(room, f.Piece)
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
		room, region, cands := f.room()
		tpl, tok := PlanInterior(room, f.Piece)
		reg, rok := PlanRegionInterior(room, f.Piece)
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
			// A dining or rec room's pieces are named by table and chair
			// index; no consumer reads which side of a table a chair is on.
			if diningRole(f.Role) && (p.Def == testDining.Chair.Def || p.Slot == "lamp") {
				continue
			}
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

func diningRole(r RoomRole) bool { return r == RoomRoleDiningRoom || r == RoomRoleRecRoom }

// Two irregular shapes per role: the plan stays on the floor, keeps the doors
// and every cell reachable, holds the role's minimum, and is no worse than the
// greedy layout on any gated term.
func TestRegionInteriorFitsIrregularFloors(t *testing.T) {
	for _, fixture := range regionFamilies() {
		for _, kind := range []string{"L", "notch"} {
			room, region, cands := irregularRoom(fixture, kind)
			key := fmt.Sprintf("%s %s %s", fixture.Role, fixture.Name, kind)
			plan, ok := PlanRegionInterior(room, fixture.Piece)
			if !ok {
				t.Errorf("%s: not furnished", key)
				continue
			}
			greedy, ok := planRegion(room, fixture.Piece, false)
			if !ok {
				t.Errorf("%s: the greedy placer does not furnish it", key)
				continue
			}
			score := func(ps []InteriorPiece) InteriorScore {
				return ScoreInterior(InteriorObjective{Region: region, Candidates: cands, Doors: room.Doors, Pieces: ps, Shapes: testShapes, Dining: testDining, Flows: objectiveFlows(t, fixture.Role)})
			}
			s := score(plan.Pieces)
			if !s.Feasible() {
				t.Errorf("%s: infeasible: %v", key, s.Violations)
			}
			if len(plan.Pieces) != len(greedy.Pieces) {
				t.Errorf("%s: search holds %d pieces, greedy %d", key, len(plan.Pieces), len(greedy.Pieces))
			}
			again, _ := PlanRegionInterior(room, fixture.Piece)
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

// A room with a bed standing off any plan keeps it: the plan holds no second
// bed, stands nothing on it, and seats the end table beside its head.
func TestRegionInteriorPlansAroundWhatStands(t *testing.T) {
	room := InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: Rectangle{X: 0, Z: 0, Width: 4, Height: 3}, Doors: []domain.Cell{{X: 4, Z: 1}}}
	bed := NewInteriorPiece("Bed_1", "Bed", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: 0, Z: 1})
	room.Built = []InteriorPiece{bed}
	plan, ok := PlanRoomInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan around the standing bed")
	}
	for _, p := range plan.Pieces {
		if p.Def == "Bed" || rectsOverlap(p.Rect, bed.Rect) {
			t.Errorf("%s stands on or beside the built bed: %+v", p.Slot, p.Rect)
		}
	}
	score := ScoreInterior(InteriorObjective{Region: RectRegion(room.Interior), Doors: room.Doors, Pieces: append([]InteriorPiece{bed}, plan.Pieces...), Shapes: testShapes})
	if len(plan.Pieces) != 1 || plan.Pieces[0].Slot != "end_table" || score.LinksServed != 1 || !score.Feasible() {
		t.Errorf("plan %+v, score %+v", plan.Pieces, score)
	}
}

// A want beside another seats each piece facing and touching its partner; a
// piece beyond the want's minimum with no partner is left out, and one the want
// requires still stands, unlinked, which the objective counts as a missed link.
func TestRegionBesideSeatsPiecesAtTheirPartner(t *testing.T) {
	region := RectRegion(Rectangle{X: 0, Z: 0, Width: 5, Height: 5})
	door := []domain.Cell{{X: 2, Z: -1}}
	table := RegionWant{Slot: "table", Def: "Table1x2c", Size: domain.Cell{X: 1, Z: 2}, Count: 1, Min: 1, Open: true}
	chairs := RegionWant{Slot: "chair", Def: "DiningChair", Size: domain.Cell{X: 1, Z: 1}, Count: 6, Beside: "table"}
	pieces, err := PlaceInRegion(RegionPlacement{Region: region, Doors: door, Wants: []RegionWant{table, chairs}})
	if err != nil {
		t.Fatal(err)
	}
	if got := defCounts(pieces)["DiningChair"]; got != 6 {
		t.Fatalf("%d chairs seated, want 6 around the open table", got)
	}
	for _, p := range pieces {
		if p.Beside != "" && !p.faces(pieces) {
			t.Errorf("%s at %+v faces %v, not the table", p.Slot, p.Rect, p.frontCells())
		}
	}
	score := ScoreInterior(InteriorObjective{Region: region, Doors: door, Pieces: pieces, Shapes: testShapes})
	if score.LinksWanted != 6 || score.LinksServed != 6 || !score.Feasible() {
		t.Errorf("links %d/%d, violations %v", score.LinksServed, score.LinksWanted, score.Violations)
	}
	// Turned away, a chair no longer links.
	turned := append([]InteriorPiece(nil), pieces...)
	for i, p := range turned {
		if p.Beside != "" {
			turned[i] = replaced(p, rotateCW(p.Rot, 2), domain.Cell{X: p.Rect.X, Z: p.Rect.Z})
			break
		}
	}
	if s := ScoreInterior(InteriorObjective{Region: region, Doors: door, Pieces: turned, Shapes: testShapes}); s.LinksServed != 5 {
		t.Errorf("a chair turned from its table serves %d of 6 links", s.LinksServed)
	}
	// No partner: an optional chair is left out, a required one stands.
	orphans := chairs
	orphans.Beside = "ghost"
	if got, _ := PlaceInRegion(RegionPlacement{Region: region, Doors: door, Wants: []RegionWant{orphans}}); len(got) != 0 {
		t.Errorf("%d chairs stand with no table to sit at", len(got))
	}
	orphans.Min = 2
	if got, err := PlaceInRegion(RegionPlacement{Region: region, Doors: door, Wants: []RegionWant{orphans}}); err != nil || len(got) != 2 {
		t.Errorf("the 2 required chairs stand: %d, %v", len(got), err)
	}
}

// A clear lane stays open floor in front of its piece, for the placer and for
// the objective.
func TestRegionClearLaneStaysOpen(t *testing.T) {
	region := RectRegion(Rectangle{X: 0, Z: 0, Width: 5, Height: 7})
	door := []domain.Cell{{X: 2, Z: -1}}
	pin := RegionWant{Slot: "pin", Def: "HorseshoesPin", Size: domain.Cell{X: 1, Z: 1}, Count: 1, Min: 1, Clear: 4, AgainstWall: true}
	crate := RegionWant{Slot: "crate", Def: "Crate", Size: domain.Cell{X: 1, Z: 1}, Count: 25}
	pieces, err := PlaceInRegion(RegionPlacement{Region: region, Doors: door, Wants: []RegionWant{pin, crate}})
	if err != nil {
		t.Fatal(err)
	}
	var lane []domain.Cell
	for _, p := range pieces {
		if p.Def == "HorseshoesPin" {
			lane = p.Reserves()
		}
	}
	if len(lane) != 4 {
		t.Fatalf("lane %v, want 4 cells", lane)
	}
	for _, p := range pieces {
		for _, c := range lane {
			if rectContains(p.Rect, c) {
				t.Errorf("%s stands on the lane cell %v", p.Slot, c)
			}
		}
	}
	if s := ScoreInterior(InteriorObjective{Region: region, Doors: door, Pieces: pieces, Shapes: testShapes}); !s.Feasible() {
		t.Errorf("a laid-out lane is infeasible: %v", s.Violations)
	}
	var blocked []InteriorPiece
	for _, p := range pieces {
		if p.Def == "HorseshoesPin" {
			blocked = append(blocked, p, NewInteriorPiece("crate", "Crate", domain.Cell{X: 1, Z: 1}, domain.North, lane[1]))
		}
	}
	if s := ScoreInterior(InteriorObjective{Region: region, Doors: door, Pieces: blocked, Shapes: testShapes}); s.Feasible() {
		t.Error("a piece on the lane is feasible")
	}
}

// regionParityIterations is the search budget the parity tests give a room with
// the station graph flows. At the default budget a flow-weighted workshop 7x5
// leaves its pair walk at the greedy layout for some seeds (about one in eight);
// four times the default reached the template or better for every seed tried.
const regionParityIterations = 4 * DefaultInteriorSearchIterations
