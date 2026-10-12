package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Moving a built room (#2865). A replan re-sites only the rooms nothing of
// ours stands on; a room that stands moves only when the colony is clearly
// better off elsewhere, and then by the successor path (layout_succession.go):
// a room of the same shape is sited, replaces the old one, and the old room
// leaves once the new one stands. Two triggers, tried for each built room of a
// successor role:
//
//   - bad ground: the room stands on rich soil (found after it was built, the
//     survey moved) and a free slot has less. The hard tier decides, so no
//     score threshold applies.
//   - score gain: the plan with the room moved scores better by the switching
//     threshold (switchThreshold): ReplanGain plus what building the room twice
//     costs, so a replan never flickers between near-equal layouts. Moving back
//     would have to clear the same bar from the other side, which the
//     difference it just gained rules out.
//
// One rebuild at a time: nothing moves while a successor is pending or another
// room is being built. The best of the rooms that trigger moves: bad ground
// before score gain, then the larger margin, ties to the lower id.

// RelocationWhy names why a successor was sited to relocate a room.
type RelocationWhy string

const (
	WhyBadGround RelocationWhy = "bad_ground"
	WhyScoreGain RelocationWhy = "score_gain"
)

// switchThreshold is the score a move of built room r must gain.
func switchThreshold(r PlannedRoom) int {
	return planWeights.ReplanGain + planWeights.BuiltSwitch + planWeights.BuiltSwitchCell*r.Region.Area()
}

// richUnder counts r's cells on rich soil.
func richUnder(soil map[domain.Cell]int, r PlannedRoom) int {
	n := 0
	for _, c := range rectCells(r.Region.Bounds()) {
		if soil[c] == soilCostRich {
			n++
		}
	}
	return n
}

// RelocationReason is why successor, a room replacing another of plan, was
// sited: the replaced room's cells hold rich soil on survey s, else the plan
// scored better with the move. "" for a room that replaces nothing.
func RelocationReason(plan LayoutPlan, successor PlannedRoom, s MapSurvey) RelocationWhy {
	i := slices.IndexFunc(plan.AllRooms(), func(r PlannedRoom) bool { return r.ID == successor.Replaces })
	if successor.Replaces == 0 || i < 0 {
		return ""
	}
	if richUnder(surveySoil(s), plan.AllRooms()[i]) > 0 {
		return WhyBadGround
	}
	return WhyScoreGain
}

// relocatableRoles are the roles a built room may be moved in: the storage and
// workshop rooms (#2865); the other successor roles move only when outgrown.
var relocatableRoles = []PlannedRole{PlannedStorage, PlannedWorkshop}

// relocatable reports a room that may be sited again as it is: a plain
// rectangle of a relocatable role in the plan's own rooms, with no link door,
// airlock or porch of its own.
func (p LayoutPlan) relocatable(r PlannedRoom) bool {
	return slices.Contains(relocatableRoles, r.Role) && !r.Unfenced() && !r.Dug && !r.Outdoor && len(p.LinksOf(r)) == 0 && r.Parent == 0 && r.Region.IsRect()
}

// relocation is one built room's move: plan with its replacement sited, the
// score the move gains and whether the room stands on bad ground.
type relocation struct {
	Plan   LayoutPlan
	Room   PlannedRoom
	Margin int
	Bad    bool
}

// worth reports a move that triggers: bad ground, or a gain past the
// switching threshold.
func (m relocation) worth() bool {
	return m.Bad || m.Margin >= switchThreshold(m.Room)
}

// relocateBuilt sites a replacement for the built room that gains most by
// moving (see the file comment). It reports whether it added one; plan is
// returned as it came otherwise. The scorer is the one over plan's zones and
// the fresh survey.
func relocateBuilt(plan LayoutPlan, sc planScorer, growth RoomGrowth, blocked map[domain.Cell]bool) (LayoutPlan, bool) {
	if growth.Built == nil || rebuildInFlight(growth) || SuccessorPending(plan) {
		return plan, false
	}
	var best *relocation
	for _, m := range relocations(plan, sc, growth.Built, blocked) {
		if m.worth() && (best == nil || m.Bad && !best.Bad || m.Bad == best.Bad && m.Margin > best.Margin) {
			best = &m
		}
	}
	if best == nil {
		return plan, false
	}
	return best.Plan, true
}

// relocations are the moves of each built, relocatable room that does not
// worsen the hard tier, in plan order.
func relocations(plan LayoutPlan, sc planScorer, built map[int32]bool, blocked map[domain.Cell]bool) []relocation {
	here := sc.core(plan)
	var out []relocation
	for _, r := range plan.Rooms {
		if !built[r.ID] || !plan.relocatable(r) {
			continue
		}
		b := r.Region.Bounds()
		sizes := [][2]int32{{b.Width, b.Height}}
		if b.Width != b.Height {
			sizes = append(sizes, [2]int32{b.Height, b.Width})
		}
		cand, ok, _ := growSuccessor(plan, Successor{Replaces: r.ID, Role: r.Role, Floor: r.Region.Area(), Sizes: sizes}, &sc, blocked)
		if !ok {
			continue
		}
		moved, _ := dropRooms(cand, map[int32]bool{r.ID: true})
		there := sc.core(moved)
		if here.Passes() && !there.Passes() || there.RoutesErr != "" && here.RoutesErr == "" {
			continue
		}
		out = append(out, relocation{Plan: cand, Room: r, Margin: there.Total() - here.Total(), Bad: richUnder(sc.g.soil, r) > 0 && there.RichCells < here.RichCells})
	}
	return out
}
