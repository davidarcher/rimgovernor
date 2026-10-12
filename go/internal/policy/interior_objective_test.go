package policy

import (
	"fmt"
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// objectiveFixture is one role at one size with a door on the south wall.
type objectiveFixture struct {
	Role RoomRole
	Name string
	W, D int32
	// Freezer adds the kitchen's second door, on the east wall, which the
	// template reads as the door to its freezer.
	Freezer bool
}

func objectiveFixtures() []objectiveFixture {
	var out []objectiveFixture
	add := func(role RoomRole, name string, size [2]int32) {
		out = append(out, objectiveFixture{Role: role, Name: name, W: size[0], D: size[1]})
		if role == RoomRoleKitchen {
			out = append(out, objectiveFixture{Role: role, Name: name + "+frz", W: size[0], D: size[1], Freezer: true})
		}
	}
	for _, c := range []struct {
		role RoomRole
		p    PlannedRole
	}{
		{RoomRoleKitchen, PlannedKitchen}, {RoomRoleDiningRoom, PlannedDining}, {RoomRoleRecRoom, PlannedDining},
		{RoomRoleHospital, PlannedHospital}, {RoomRolePrisonCell, PlannedPrison}, {RoomRoleWorkshop, PlannedWorkshop},
		{RoomRoleLaboratory, PlannedLab}, {RoomRoleTomb, PlannedTomb},
	} {
		add(c.role, "core", coreRoomSize[c.p])
		for _, v := range coreRoomVariants[c.p] {
			add(c.role, "variant", v)
		}
	}
	for _, tier := range []TechTier{TechTierCamp, TechTierPowered, TechTierSpacer} {
		add(RoomRoleBedroom, fmt.Sprintf("wing/%d", tier), WingRoomSize(tier))
	}
	return out
}

// objectiveRoom is the fixture's room, its region, and its candidate doors
// (every wall cell but the corners). The one door is on the south wall.
func objectiveRoom(f objectiveFixture) (InteriorRoom, Region, []domain.Cell) {
	r := Rectangle{X: 10, Z: 20, Width: f.W, Height: f.D}
	door := domain.Cell{X: r.X + (f.W-1)/2, Z: r.Z - 1}
	var cands []domain.Cell
	for x := r.X; x < r.X+r.Width; x++ {
		cands = append(cands, domain.Cell{X: x, Z: r.Z - 1}, domain.Cell{X: x, Z: r.Z + r.Height})
	}
	for z := r.Z; z < r.Z+r.Height; z++ {
		cands = append(cands, domain.Cell{X: r.X - 1, Z: z}, domain.Cell{X: r.X + r.Width, Z: z})
	}
	doors := []domain.Cell{door}
	if f.Freezer {
		doors = append(doors, domain.Cell{X: r.X + r.Width, Z: r.Z})
	}
	room := InteriorRoom{Role: f.Role, Interior: r, Doors: doors, Dining: testDining, Shapes: testShapes}
	return room, RectRegion(r), cands
}

// greedyWants asks the placer for the template's inventory: every definition
// it placed, as many times, in first-seen order.
func greedyWants(pieces []InteriorPiece) ([]RegionWant, map[string]int) {
	var wants []RegionWant
	index := map[string]int{}
	required := map[string]int{}
	for _, p := range pieces {
		required[p.Def]++
		i, ok := index[p.Def]
		if !ok {
			i = len(wants)
			index[p.Def] = i
			w := RegionWant{Slot: p.Def, Def: p.Def, Size: p.Size}
			if p.InteractionOffset != nil {
				off := *p.InteractionOffset
				w.Interaction = &off
			}
			wants = append(wants, w)
		}
		wants[i].Count++
	}
	return wants, required
}

type objectiveRow struct {
	Fixture          objectiveFixture
	Template, Greedy *InteriorScore
}

func scoreObjectiveFixtures(t *testing.T) []objectiveRow {
	var rows []objectiveRow
	for _, f := range objectiveFixtures() {
		room, region, cands := objectiveRoom(f)
		row := objectiveRow{Fixture: f}
		plan, ok := PlanInterior(room, InteriorPieceDef{})
		if !ok {
			rows = append(rows, row)
			continue
		}
		wants, required := greedyWants(plan.Pieces)
		in := InteriorObjective{Region: region, Candidates: cands, Doors: room.Doors, Pieces: plan.Pieces, Required: required, Shapes: testShapes, Dining: testDining, Flows: objectiveFlows(t, f.Role)}
		ts := ScoreInterior(in)
		row.Template = &ts
		placed, _ := PlaceInRegion(RegionPlacement{Region: region, Doors: room.Doors, Wants: wants})
		in.Pieces = placed
		gs := ScoreInterior(in)
		row.Greedy = &gs
		rows = append(rows, row)
	}
	return rows
}

func objectiveCells(s *InteriorScore) string {
	if s == nil {
		return "-"
	}
	v := ""
	if !s.Feasible() {
		v = fmt.Sprintf(" INFEASIBLE(%d)", len(s.Violations))
	}
	return fmt.Sprintf("n=%d free=%.2f links=%d/%d gated walk=%.2f (door %.2f pair %.2f, %d edges) info walk=%.2f (%d edges)%s", s.Pieces, s.Free(), s.LinksServed, s.LinksWanted, s.Walk, s.DoorWalk, s.PairWalk, s.WalkEdges, s.InfoWalk, s.InfoEdges, v)
}

// TestInteriorObjectiveScoresTemplatesAndGreedy prints the stage-1 score
// table (go test -run Objective -v) and holds the templates to the hard
// constraints and to no gated term (free floor, links, pair walk, door walk of
// recipe and item flows) worse than the greedy placer; informational walking
// (meals, sleep, rest) is printed and not compared.
func TestInteriorObjectiveScoresTemplatesAndGreedy(t *testing.T) {
	var table strings.Builder
	for _, row := range scoreObjectiveFixtures(t) {
		f := row.Fixture
		fmt.Fprintf(&table, "%-12s %-9s %2dx%d  template: %s | greedy: %s\n", f.Role, f.Name, f.W, f.D, objectiveCells(row.Template), objectiveCells(row.Greedy))
		if row.Template == nil {
			continue
		}
		if !row.Template.Feasible() {
			t.Errorf("%s %s %dx%d: template breaks hard constraints: %v", f.Role, f.Name, f.W, f.D, row.Template.Violations)
		}
		key := fmt.Sprintf("%s %s %dx%d", f.Role, f.Name, f.W, f.D)
		worse := row.Template.WorseTerms(*row.Greedy)
		known, listed := knownTemplateGaps[key]
		switch {
		case !row.Greedy.Feasible():
		case listed:
			if len(worse) != 1 || worse[0] != "door_walk" || math.Abs(row.Template.DoorWalk-known.template) > 1e-6 || math.Abs(row.Greedy.DoorWalk-known.greedy) > 1e-6 {
				t.Errorf("%s: known gap changed: worse on %v, door walk template %.2f greedy %.2f, recorded template %.2f greedy %.2f",
					key, worse, row.Template.DoorWalk, row.Greedy.DoorWalk, known.template, known.greedy)
			}
		case len(worse) > 0:
			t.Errorf("%s: template worse than greedy on %v", key, worse)
		}
	}
	t.Log("\n" + table.String())
}

// knownTemplateGaps are the only fixtures where a template scores worse than
// the greedy placer on a gated term, recorded with their door-walk numbers so
// that any other regression, or any change in these numbers, fails the test.
//
// All four are the Kitchen: kitchenBackRow puts the stoves on the back wall
// facing the entrance, far from the door, while the greedy placer puts them
// at the threshold. The stove-to-door flows (ingredients in, meals out) are
// recipe flows, so the door walk is gated. No game-derived reason favours the
// template: nothing in RimWorld penalises a stove near a door (see the commit
// body). This is the stage-2 target, where the search must beat both the
// template and the greedy placer. Delete an entry when its gap closes.
var knownTemplateGaps = map[string]struct{ template, greedy float64 }{
	"Kitchen core 6x5":        {4.50, 3.00},
	"Kitchen core+frz 6x5":    {4.00, 1.00},
	"Kitchen variant 5x6":     {4.00, 1.00},
	"Kitchen variant+frz 5x6": {5.00, 1.00},
}

func TestInteriorObjectiveHardConstraints(t *testing.T) {
	region := RectRegion(Rectangle{X: 0, Z: 0, Width: 4, Height: 3})
	door := domain.Cell{X: 1, Z: -1}
	bed := func(slot string, x, z int32) InteriorPiece {
		return NewInteriorPiece(slot, "Bed", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: x, Z: z})
	}
	base := InteriorObjective{Region: region, Doors: []domain.Cell{door}, Candidates: []domain.Cell{door}, Shapes: testShapes}
	cases := []struct {
		name   string
		mutate func(*InteriorObjective)
		want   string
	}{
		{"clean", func(in *InteriorObjective) { in.Pieces = []InteriorPiece{bed("a", 3, 0)} }, ""},
		{"outside", func(in *InteriorObjective) { in.Pieces = []InteriorPiece{bed("a", 3, 2)} }, "outside the region"},
		{"overlap", func(in *InteriorObjective) { in.Pieces = []InteriorPiece{bed("a", 3, 0), bed("b", 3, 1)} }, "overlap"},
		{"threshold", func(in *InteriorObjective) { in.Pieces = []InteriorPiece{bed("a", 1, 0)} }, "inside the door"},
		{"not candidate", func(in *InteriorObjective) { in.Doors = []domain.Cell{{X: 2, Z: -1}} }, "not a candidate"},
		{"no door", func(in *InteriorObjective) { in.Doors = nil }, "no door"},
		{"cut off", func(in *InteriorObjective) {
			// A wall of beds across the room leaves the far column unreachable.
			in.Pieces = []InteriorPiece{bed("a", 2, 0), bed("b", 2, 1)}
			in.Pieces[1] = NewInteriorPiece("b", "Dresser", domain.Cell{X: 2, Z: 1}, domain.North, domain.Cell{X: 2, Z: 2})
			in.Pieces = append(in.Pieces, NewInteriorPiece("c", "Dresser", domain.Cell{X: 2, Z: 1}, domain.North, domain.Cell{X: 0, Z: 2}))
			in.Pieces = append(in.Pieces, bed("d", 1, 1))
		}, "cut off"},
		{"required", func(in *InteriorObjective) { in.Required = map[string]int{"Bed": 1} }, "required"},
	}
	for _, c := range cases {
		in := base
		c.mutate(&in)
		s := ScoreInterior(in)
		joined := strings.Join(s.Violations, "; ")
		if c.want == "" && !s.Feasible() || c.want != "" && !strings.Contains(joined, c.want) {
			t.Errorf("%s: violations %q, want %q", c.name, joined, c.want)
		}
	}
}

func TestInteriorObjectiveSoftTerms(t *testing.T) {
	region := RectRegion(Rectangle{X: 0, Z: 0, Width: 5, Height: 4})
	door := domain.Cell{X: 2, Z: -1}
	bed := NewInteriorPiece("bed", "Bed", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: 0, Z: 0})
	near := NewInteriorPiece("table", "EndTable", domain.Cell{X: 1, Z: 1}, domain.North, domain.Cell{X: 1, Z: 0})
	far := NewInteriorPiece("table", "EndTable", domain.Cell{X: 1, Z: 1}, domain.North, domain.Cell{X: 4, Z: 3})
	var flows InteriorFlows
	score := func(pieces ...InteriorPiece) InteriorScore {
		return ScoreInterior(InteriorObjective{Region: region, Doors: []domain.Cell{door}, Pieces: pieces, Shapes: testShapes, Flows: flows})
	}
	linked, unlinked := score(bed, near), score(bed, far)
	if linked.LinksServed != 1 || unlinked.LinksServed != 0 || linked.LinksWanted != 1 {
		t.Errorf("links: linked %d/%d, unlinked %d/%d", linked.LinksServed, linked.LinksWanted, unlinked.LinksServed, unlinked.LinksWanted)
	}
	if !linked.NoWorseThan(unlinked) || unlinked.NoWorseThan(linked) {
		t.Errorf("linked layout is not strictly better: %v", unlinked.WorseTerms(linked))
	}
	flows = objectiveFlows(t, RoomRoleBedroom)
	if s := score(bed); s.FreeCells != 18 || s.InfoWalk <= 0 || s.Walk != 0 || s.Beauty != 0 || len(s.Unknown) != 1 {
		t.Errorf("one bed: %+v", s)
	}
	nearDoor := score(NewInteriorPiece("a", "Bed", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: 2, Z: 0}))
	farFromDoor := score(NewInteriorPiece("a", "Bed", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: 4, Z: 2}))
	if nearDoor.InfoWalk >= farFromDoor.InfoWalk {
		t.Errorf("info walk: near %v, far %v", nearDoor.InfoWalk, farFromDoor.InfoWalk)
	}
}
