package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recorded royalty read: Alice holds Yeoman, the Knight rung asks for a 30
// cell room at impressiveness 55 with a Throne.
func throneRoyalty() domain.Fact[policy.RoyaltyFacts] {
	return domain.Known(policy.RoyaltyFacts{
		Ladder: []policy.RoyalRung{
			{Title: "Yeoman", FavorNeeded: domain.Known(6)},
			{Title: "Knight", FavorNeeded: domain.Known(10), ThroneMinArea: domain.Known(30), ThroneMinImpressiveness: domain.Known(55), ThroneThings: []string{"Throne"}, ThroneAssigned: domain.Known(true)},
		},
		Holders: map[policy.PawnID][]policy.RoyalHolding{"Alice": {{FactionDef: "Empire", Title: "Yeoman", Favor: domain.Known(2)}}},
	})
}

func throneProjection(standing bool) (observation.ColonyProjection, policy.LayoutRoom) {
	room := policy.LayoutRoom{Role: policy.ModuleThrone, Interior: policy.Rectangle{X: 10, Z: 20, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	var rooms policy.RoomObservation
	if standing {
		var cells []domain.Cell
		for z := room.Interior.Z; z < room.Interior.Z+room.Interior.Height; z++ {
			for x := room.Interior.X; x < room.Interior.X+room.Interior.Width; x++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
		rooms.Rooms = []policy.Room{{ID: "r1", Enclosed: domain.Known(true), Cells: cells}}
	}
	facts := observation.ColonyProjection{
		LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.LayoutRoom{room}}),
		Rooms:      domain.Known(rooms),
		Royalty:    throneRoyalty(),
		Definitions: []observation.PlanningDefinition{{
			Name: "Throne", Available: domain.Known(true), Size: domain.Known(policy.Bounds{Width: 1, Height: 1}),
		}},
	}
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	facts.Facts.Sleeping = domain.Known(policy.SleepingObservation{})
	return facts, room
}

// The throne room rides under MaintainHousing: shelled, then furnished with
// the throne, each owing the bedroom phase, and unknown or absent royalty
// owes nothing.
func TestThroneRoomOwesHousingUntilTheThroneStands(t *testing.T) {
	facts, room := throneProjection(false)
	if step := throneStep(facts); step.Kind != policy.ThroneShell || step.Room != room {
		t.Fatalf("unbuilt room: %+v", step)
	}
	if owed, known := bedroomsOwed(facts).Value(); !known || !owed {
		t.Fatalf("the shell is owed: %v %v", owed, known)
	}
	facts, _ = throneProjection(true)
	step := throneStep(facts)
	if step.Kind != policy.ThronePlace || step.Piece.Def != "Throne" {
		t.Fatalf("standing room: %+v", step)
	}
	if owed, known := bedroomsOwed(facts).Value(); !known || !owed {
		t.Fatalf("the throne is owed: %v %v", owed, known)
	}
	// With the throne standing only the assignment seam remains, never owed.
	throne, err := domain.NewBuilding("Throne", step.Piece.Anchor(), step.Piece.Rot, "")
	if err != nil {
		t.Fatal(err)
	}
	census := policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "t1", Building: throne}}}
	census.Buildings[0].Cells = []domain.Cell{step.Piece.Anchor()}
	facts.Facts.CurrentConstruction = domain.Known(census)
	if step := throneStep(facts); step.Kind != policy.ThroneAssign || step.Owed() {
		t.Fatalf("throne standing: %+v", step)
	}
	if owed, known := bedroomsOwed(facts).Value(); known && owed {
		t.Fatal("assignment awaits an action kind and is not owed")
	}
	facts.Royalty = domain.Unknown[policy.RoyaltyFacts]()
	if step := throneStep(facts); step.Kind != policy.ThroneNone {
		t.Fatalf("unknown royalty owes no room: %+v", step)
	}
}

// The standing throne room gets the title's impressiveness as a room
// quality target, which the room upgrade and the beauty upgrade furnish.
func TestThroneTargetsJoinTheRoomQualityTargets(t *testing.T) {
	facts, _ := throneProjection(true)
	targets := withThroneTargets(facts, nil)
	if got := targets["r1"]; got.Min != 55 || got.Reasons[0] != "title" {
		t.Fatalf("%+v", targets)
	}
	own := map[string]policy.RoomTarget{"bed": {Room: "bed", Min: 30}}
	if got := withThroneTargets(facts, own); len(got) != 2 || got["bed"].Min != 30 {
		t.Fatalf("%+v", got)
	}
}

// A title that asks for a throne room the plan lacks grows one at the next
// hourly layout review, sized to the title's area; the ladder's throne
// definitions name what the planners read.
func TestLayoutGrowsAThroneRoomForTheNextTitle(t *testing.T) {
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	survey := openSurvey(120)
	reads := 0
	r := &RoutineReviewer{player: s.player, native: countingSurvey{survey: survey, reads: &reads}}
	snapshot := s.player.session.State().Snapshot
	projection := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load}, Bounds: survey.Bounds}
	projection.Facts.Colonists = domain.Known(int64(3))
	projection.BuildTier = domain.Known(policy.BuildTierCamp)
	review := func(tick domain.Tick) policy.LayoutPlan {
		t.Helper()
		projection.Identity.Tick = tick
		if err := r.reviewLayoutPlan(ctx, snapshot, &projection); err != nil {
			t.Fatal(err)
		}
		layout, ok, err := r.layoutPlan(ctx, snapshot, tick)
		if err != nil || !ok {
			t.Fatal("no plan", err)
		}
		return layout.Plan
	}
	if plan := review(100); len(plan.Rooms) == 0 {
		t.Fatal("no plan derived")
	}
	for tick := domain.Tick(2700); tick <= 5400; tick += 2700 {
		review(tick)
	}
	projection.Royalty = throneRoyalty()
	plan := review(8200)
	room, ok := plan.ThroneRoomFor(30)
	if !ok {
		t.Fatal("no throne room grown for the Knight rung")
	}
	if got := room.Interior.Width * room.Interior.Height; got < 30 {
		t.Fatalf("throne room of %d cells", got)
	}
	royalty, _ := projection.Royalty.Value()
	if names := throneThings(royalty); len(names) != 1 || names[0] != "Throne" {
		t.Fatalf("throne definitions %v", names)
	}
}
