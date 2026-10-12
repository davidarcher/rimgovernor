package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// The replan is the tiled plan (#3005): a colony whose rooms the census leaves
// unread (every room held) replans to a valid plan that loses none of them.
func TestReplanLayoutKeepsTheHeldRooms(t *testing.T) {
	slowtest.Skip(t, "a full derive and replan; runs under cmd/test -full and nightly")
	f := replayFixtures[0]
	s := f.survey(t)
	plan, facts, _ := builtReplanAdvice(t, f)
	next, _, err := ReplanLayout(plan, facts, s, nil, RoomGrowth{}, 0)
	if err != nil {
		t.Fatal(err)
	}
	if !next.Valid() {
		t.Fatalf("invalid replan: %s", next.Summary())
	}
	have := map[int32]bool{}
	for _, r := range next.Rooms {
		have[r.ID] = true
	}
	for _, r := range plan.Rooms {
		if !r.attached() && !have[r.ID] {
			t.Errorf("room %d (%s) left a plan whose rooms are all held", r.ID, r.Role)
		}
	}
}

// A standing room the new embedding has no node for leaves the plan once the
// colony holds nothing in it, and its ground is retired for clearance.
func TestReplanLayoutRetiresAStandingRoomWithNoNode(t *testing.T) {
	slowtest.Skip(t, "a full derive and replan; runs under cmd/test -full and nightly")
	f := replayFixtures[0]
	s := f.survey(t)
	plan, facts, _ := builtReplanAdvice(t, f)
	// The colony shrinks to one colonist and nothing is held (the census is read
	// and nothing stands on a room): the bedrooms the sleepers no longer need
	// have no node in the new embedding.
	small := colonyStationFacts(1, s)
	small.Demand, small.Child, small.Built = facts.Demand, facts.Child, facts.Built
	next, _, err := ReplanLayout(plan, small, s, nil, RoomGrowth{Fixed: map[int32]bool{}}, 0)
	if err != nil {
		t.Fatal(err)
	}
	have, dropped := map[int32]bool{}, 0
	for _, r := range next.Rooms {
		have[r.ID] = true
	}
	for _, r := range plan.Rooms {
		if r.attached() || have[r.ID] {
			continue
		}
		dropped++
		ground := RectRegion(roomGround(r.Region.Bounds()))
		if !slices.ContainsFunc(next.RetiredGround, ground.Equal) {
			t.Errorf("the dropped room %d (%s) left no retired ground %v", r.ID, r.Role, next.RetiredGround)
		}
	}
	if dropped == 0 {
		t.Fatal("a colony of one kept every bedroom of a larger plan")
	}
}

// A chain that cannot tile keeps the plan the colony has and says why.
func TestReplanLayoutKeepsThePlanOnAnError(t *testing.T) {
	slowtest.Skip(t, "a full derive and replan; runs under cmd/test -full and nightly")
	f := replayFixtures[0]
	s := f.survey(t)
	plan, facts, _ := builtReplanAdvice(t, f)
	facts.Colonists = domain.Unknown[int64]()
	next, _, err := ReplanLayout(plan, facts, s, nil, RoomGrowth{}, 0)
	if err == nil {
		t.Fatal("an unread colony replanned")
	}
	if !SamePlan(plan, next) {
		t.Error("the plan changed with the replan")
	}
}
