package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func freshSurvey() MapSurvey {
	return zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
}

// #957: with nothing built the fresh replan is the derived plan.
func TestReplanFreshWithNothingBuiltDerives(t *testing.T) {
	s := freshSurvey()
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	fresh, ok := ReplanFresh(plan, s, nil, 3, plan.TombRooms(), BuildTierCamp, nil, 30).Value()
	if !ok || !reflect.DeepEqual(fresh, plan) {
		t.Fatalf("fresh replan of an unbuilt plan differs: ok=%t\n%s\n%s", ok, fresh.Summary(), plan.Summary())
	}
}

// #957: a room with anything built on it stays where it is with the
// hallway its door opens onto; unstarted rooms re-grow, off built cells.
func TestReplanFreshKeepsBuiltRoomsAndRegrowsTheRest(t *testing.T) {
	s := freshSurvey()
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	// A room off a crossing when there is one, so the kept hallway is not
	// only the main one.
	pick := -1
	for i, r := range plan.Rooms {
		for j, sg := range plan.Spine {
			if j > 0 && onSegment(r, sg) {
				pick = i
			}
		}
	}
	if pick < 0 {
		pick = len(plan.Rooms) - 1
	}
	built := plan.Rooms[pick]
	// Partly built: its door and one wall cell stand.
	cells := map[domain.Cell]bool{built.Door: true, {X: built.Interior.X - 1, Z: built.Interior.Z - 1}: true}
	// An unstarted room the plan no longer wants, and a building standing
	// on open ground where nothing is planned.
	stale := LayoutRoom{Role: ModuleReserve, Interior: Rectangle{X: 20, Z: 20, Width: 3, Height: 3}, Door: domain.Cell{X: 21, Z: 19}, DoorRot: domain.South}
	current := plan
	current.Rooms = append(slices.Clone(plan.Rooms), stale)
	fresh, ok := ReplanFresh(current, s, cells, 5, current.TombRooms(), BuildTierCamp, nil, 30).Value()
	if !ok || !fresh.Valid() {
		t.Fatal("no fresh plan", ok)
	}
	if !slices.Contains(fresh.Rooms, built) {
		t.Fatal("the built room moved or was dropped")
	}
	if slices.Contains(fresh.Rooms, stale) {
		t.Fatal("the unstarted stale room was kept")
	}
	opens := false
	for _, sg := range fresh.Spine {
		opens = opens || onSegment(built, sg)
	}
	if !opens {
		t.Fatal("the built room's hallway was dropped")
	}
	if fresh.LayoutOutgrown(5) {
		t.Fatal("the unstarted rooms did not re-grow for 5 colonists")
	}
	if _, err := CheckRoutes(fresh); err != nil {
		t.Fatal(err)
	}
}

// A built cell no kept room covers keeps new rooms off it.
func TestReplanFreshKeepsNewRoomsOffBuiltGround(t *testing.T) {
	s := freshSurvey()
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	// Mark the floor of every room but the first as built ground that no
	// kept room covers: drop those rooms from the plan first.
	cells := map[domain.Cell]bool{}
	for _, r := range plan.Rooms[1:] {
		c := domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}
		cells[c] = true
	}
	current := plan
	current.Rooms = plan.Rooms[:1]
	fresh, ok := ReplanFresh(current, s, cells, 3, 1, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no fresh plan")
	}
	for _, r := range fresh.Rooms {
		if r != plan.Rooms[0] && rectHits(r.Interior, cells) {
			t.Fatalf("a new %s stands on a built cell", r.Role)
		}
	}
}

func TestProposalOverlayTintsAddedAndDroppedRooms(t *testing.T) {
	kept := LayoutRoom{Role: ModuleKitchen, Interior: Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 9}, DoorRot: domain.South}
	gone := LayoutRoom{Role: ModuleBedroom, Interior: Rectangle{X: 20, Z: 10, Width: 5, Height: 5}, Door: domain.Cell{X: 22, Z: 9}, DoorRot: domain.South}
	added := LayoutRoom{Role: ModuleLab, Interior: Rectangle{X: 30, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 32, Z: 9}, DoorRot: domain.South}
	current := LayoutPlan{Rooms: []LayoutRoom{kept, gone}, Spine: []SpineSegment{{From: domain.Cell{X: 10, Z: 7}, To: domain.Cell{X: 25, Z: 7}}}}
	next := LayoutPlan{Rooms: []LayoutRoom{kept, added}, Spine: []SpineSegment{{From: domain.Cell{X: 10, Z: 7}, To: domain.Cell{X: 35, Z: 7}}}}
	if a, r := LayoutProposalDiff(current, next); a != 1 || r != 1 {
		t.Fatal(a, r)
	}
	o := ProposalOverlay(current, next, Bounds{Width: 100, Height: 100})
	var labels []string
	for _, l := range o.Labels {
		labels = append(labels, l.Text)
	}
	if !slices.Equal(labels, []string{"-bedroom", "+research"}) {
		t.Fatal(labels)
	}
	if len(o.Layers) != 2 || o.Layers[0].Color.R < o.Layers[0].Color.G || o.Layers[1].Color.G < o.Layers[1].Color.R {
		t.Fatalf("want a red then a green layer: %+v", o.Layers)
	}
	// The dropped room's walls and the added hallway stretch are tinted.
	if len(o.Layers[0].Rects) != 1 || o.Layers[0].Rects[0] != roomWalls(gone) || len(o.Layers[1].Runs) == 0 {
		t.Fatalf("%+v", o.Layers)
	}
	if o := ProposalOverlay(current, current, Bounds{Width: 100, Height: 100}); len(o.Layers)+len(o.Labels) != 0 {
		t.Fatal("an unchanged proposal drew", o)
	}
}
