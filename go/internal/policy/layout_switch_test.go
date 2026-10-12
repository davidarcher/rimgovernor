package policy

import (
	"slices"
	"testing"
)

// switchFixture is the baseline plan for three colonists with every room
// built, its survey, and its storeroom.
func switchFixture(t *testing.T) (MapSurvey, LayoutPlan, PlannedRoom, RoomGrowth) {
	t.Helper()
	s := loadSurvey(t, baselineSurveyPath)
	plan, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	var store PlannedRoom
	for _, r := range plan.Rooms {
		if r.Role == PlannedStorage {
			store = r
		}
	}
	if !relocatable(store) {
		t.Fatalf("storeroom %+v is not relocatable", store)
	}
	all := allPins(plan)
	return s, plan, store, RoomGrowth{Fixed: all, Built: all}
}

// richUnderRoom is s with rich soil under every cell of r.
func richUnderRoom(s MapSurvey, r PlannedRoom) MapSurvey {
	s.Cells = slices.Clone(s.Cells)
	for i, c := range s.Cells {
		if r.Region.Contains(c.Cell) {
			s.Cells[i].Rock, s.Cells[i].Fertility = false, 1.4
		}
	}
	return s
}

func TestBadGroundRetiresABuiltRoomOnRichSoil(t *testing.T) {
	s, plan, store, growth := switchFixture(t)

	// The survey as built: nothing moves.
	calm, _, _ := ReplanLayoutWithRooms(plan, s, growth, 0, 3, 0, TechTierCamp, nil, nil)
	if SuccessorPending(calm) {
		t.Fatal("a plan on plain ground planned a relocation")
	}

	bad := richUnderRoom(s, store)
	next, changed, err := ReplanLayoutWithRooms(plan, bad, growth, 0, 3, 0, TechTierCamp, nil, nil)
	if err != nil || !changed || !next.Valid() {
		t.Fatalf("changed %v valid %v err %v", changed, next.Valid(), err)
	}
	var added *PlannedRoom
	for _, r := range next.AllRooms() {
		if r.Replaces == store.ID {
			added = &r
		}
	}
	if added == nil || added.Role != PlannedStorage || added.Region.Area() < store.Region.Area() || added.Region.Overlaps(store.Region) {
		t.Fatalf("replacement = %+v", added)
	}
	if why := RelocationReason(next, *added, bad); why != WhyBadGround {
		t.Fatalf("reason = %q", why)
	}
	if why := RelocationReason(next, *added, s); why != WhyScoreGain {
		t.Fatalf("reason on plain ground = %q", why)
	}
	// The old room stays, pinned, until the replacement stands, then goes to clearance.
	pending := allPins(next)
	delete(pending, added.ID)
	if again, _, _ := ReplanLayoutWithRooms(next, bad, RoomGrowth{Fixed: pending, Built: pending}, 0, 3, 0, TechTierCamp, nil, nil); !slices.ContainsFunc(again.AllRooms(), func(r PlannedRoom) bool { return r.ID == store.ID }) {
		t.Fatal("the old room left before its replacement stood")
	}
	built := allPins(next)
	done, _, _ := ReplanLayoutWithRooms(next, bad, RoomGrowth{Fixed: built, Built: built}, 0, 3, 0, TechTierCamp, nil, nil)
	if slices.ContainsFunc(done.AllRooms(), func(r PlannedRoom) bool { return r.ID == store.ID }) {
		t.Fatal("the old room stayed with its replacement standing")
	}
	// And it settles: the replacement is clear of rich soil, so nothing moves again.
	all := allPins(done)
	if settled, _, _ := ReplanLayoutWithRooms(done, bad, RoomGrowth{Fixed: all, Built: all}, 0, 3, 0, TechTierCamp, nil, nil); SuccessorPending(settled) {
		t.Fatal("a settled colony planned another relocation")
	}
}

func TestNoRelocationWhileAnotherRoomIsBuilding(t *testing.T) {
	s, plan, store, growth := switchFixture(t)
	building := allPins(plan)
	delete(building, plan.Rooms[0].ID)
	if plan.Rooms[0].ID == store.ID {
		delete(building, plan.Rooms[1].ID)
	}
	next, _, _ := ReplanLayoutWithRooms(plan, richUnderRoom(s, store), RoomGrowth{Fixed: growth.Fixed, Built: building}, 0, 3, 0, TechTierCamp, nil, nil)
	if SuccessorPending(next) {
		t.Fatal("a second rebuild started while a room was being built")
	}
	// A census that is not known keeps every room.
	next, _, _ = ReplanLayoutWithRooms(plan, richUnderRoom(s, store), RoomGrowth{}, 0, 3, 0, TechTierCamp, nil, nil)
	if SuccessorPending(next) {
		t.Fatal("moved a room on an unknown census")
	}
}

func TestScoreGainMovesABuiltRoomOnlyAboveTheSwitchingThreshold(t *testing.T) {
	s, plan, store, growth := switchFixture(t)
	// Rich soil under the room makes the move the best available; the plain-soil
	// score gain of the same move is the margin to test the bar against.
	bad := richUnderRoom(s, store)
	plan.Zones = Zone(bad)
	sc := newPlanScorer(plan.Zones, plan.Reservations, bad)
	var move *relocation
	for _, m := range relocations(plan, sc, growth.Built, blockedCells(plan, nil)) {
		if m.Room.ID == store.ID {
			move = &m
		}
	}
	if move == nil || move.Margin <= 0 {
		t.Fatalf("no profitable move for the storeroom: %+v", move)
	}
	defer func(w int) { planWeights.BuiltSwitch = w }(planWeights.BuiltSwitch)

	// Score gain alone, bad ground set aside: use the plain survey with the
	// move's margin as a bar. A bar one above the margin refuses; at it, moves.
	m := *move
	m.Bad = false
	planWeights.BuiltSwitch = m.Margin + 1 - switchThreshold(m.Room) + planWeights.BuiltSwitch
	if m.worth() {
		t.Fatalf("a gain of %d cleared a threshold of %d", m.Margin, switchThreshold(m.Room))
	}
	planWeights.BuiltSwitch--
	if !m.worth() {
		t.Fatalf("a gain of %d did not clear a threshold of %d", m.Margin, switchThreshold(m.Room))
	}
	// The threshold prices the move: a larger room asks more.
	big := m.Room
	big.Region = rect(0, 0, 12, 12)
	if switchThreshold(big) <= switchThreshold(m.Room) {
		t.Fatal("a larger room does not cost more to move")
	}
}

func TestScoreGainDoesNotOscillate(t *testing.T) {
	s, plan, store, _ := switchFixture(t)
	bad := richUnderRoom(s, store)
	// Rich soil forces one move. Once the replacement stands, moving back
	// would put the room on the rich soil again, so repeated replans settle.
	cur, all := plan, allPins(plan)
	moves := 0
	for range 4 {
		next, _, _ := ReplanLayoutWithRooms(cur, bad, RoomGrowth{Fixed: all, Built: all}, 0, 3, 0, TechTierCamp, nil, nil)
		if !SuccessorPending(next) {
			break
		}
		moves++
		all = allPins(next)
		cur, _, _ = ReplanLayoutWithRooms(next, bad, RoomGrowth{Fixed: all, Built: all}, 0, 3, 0, TechTierCamp, nil, nil)
		all = allPins(cur)
	}
	if moves != 1 {
		t.Fatalf("%d relocations over repeated replans, want exactly 1", moves)
	}
}

func TestBadGroundIgnoresTheSwitchingBar(t *testing.T) {
	s, plan, store, growth := switchFixture(t)
	defer func(a int) { planWeights.BuiltSwitch = a }(planWeights.BuiltSwitch)
	planWeights.BuiltSwitch = 1 << 30
	if next, _, _ := ReplanLayoutWithRooms(plan, s, growth, 0, 3, 0, TechTierCamp, nil, nil); SuccessorPending(next) {
		t.Fatal("a room moved on plain ground")
	}
	next, _, _ := ReplanLayoutWithRooms(plan, richUnderRoom(s, store), growth, 0, 3, 0, TechTierCamp, nil, nil)
	if !SuccessorPending(next) {
		t.Fatal("a room on rich soil waited for the score bar")
	}
}
