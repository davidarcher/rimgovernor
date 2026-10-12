package policy

import (
	"maps"
	"slices"
)

// The built world in the embedding (#2877). A room that stands is a fixed
// block: the group MatchGroupRooms gives it keeps its interior, and the
// embedding places the other groups around it. The only way a built room moves
// is the relocation pass (embedder.relocate), which prices the move the way
// the old planner does (layout_switch.go) and so adds no constant:
//
//   - score gain: the room's best other position, of the same shape, must beat
//     staying by switchThreshold: ReplanGain plus BuiltSwitch and BuiltSwitchCell
//     per floor cell (the walls, floor and furniture built twice). That price
//     already counts the demolished walls and built furniture, so no second
//     wall or furniture term is added; what stands inside a room is #2881.
//   - bad ground: the room stands on rich soil and a position has less. The hard
//     tier decides, so the price is waived.
//
// One rebuild at a time: at most one room moves, the best (bad ground before
// score gain, then the larger margin, ties to the lower group), and none while
// a rebuild is in flight (EmbedInput.InFlight). A pinned room, one the old
// planner would not move (replanPins), never does. Ground the plan retired but
// has not cleared (EmbedInput.RetiredGround) is closed to new blocks. A moved
// room's block carries Replaces, the successor path's id of the room it
// supersedes; the old room is not in the embedding, it stands until the new
// one does. Conflict pricing (merge, split, lost room effects) is #2878.

// Verdicts and reasons of built-room decisions.
const (
	EmbedKeep = "keep"
	EmbedMove = "move"

	ReasonEmbedBuiltKept      = "embed_built_kept"
	ReasonEmbedBuiltPinned    = "embed_built_pinned"
	ReasonEmbedBuiltInFlight  = "embed_built_in_flight"
	ReasonEmbedBuiltBelow     = "embed_built_below_threshold"
	ReasonEmbedBuiltDeferred  = "embed_built_one_rebuild_at_a_time"
	ReasonEmbedBuiltBadGround = "embed_built_moved_bad_ground"
	ReasonEmbedBuiltMovedGain = "embed_built_moved_score_gain"
	ReasonEmbedBuiltOffMap    = "embed_built_off_map"
	ReasonEmbedBuiltNotRect   = "embed_built_not_a_block"
)

// builtHomes picks, for each room group, the standing room that is its block:
// the largest rectangular room MatchGroupRooms gave it (ties to the lower id).
// Rooms matched to a group beyond that one are not modelled (the group has one
// block), and a room that is no rectangle is no block.
func builtHomes(groups []RoomGroup, built []BuiltRegion) (home []Rectangle, room []int32, decisions []EmbedDecision) {
	home, room = make([]Rectangle, len(groups)), make([]int32, len(groups))
	byID := map[int32]BuiltRegion{}
	for _, b := range built {
		byID[b.Room] = b
	}
	for _, m := range MatchGroupRooms(groups, built) {
		if len(m.Rooms) == 0 {
			continue
		}
		var pick *BuiltRegion
		for _, id := range m.Rooms {
			b := byID[id]
			if !b.Region.IsRect() {
				decisions = append(decisions, EmbedDecision{Group: m.Group, Other: -1, Verdict: EmbedKeep, Reason: ReasonEmbedBuiltNotRect, Room: id})
				continue
			}
			if pick == nil || b.Region.Area() > pick.Region.Area() {
				pick = &b
			}
		}
		if pick != nil {
			home[m.Group], room[m.Group] = pick.Region.Bounds(), pick.Room
		}
	}
	return home, room, decisions
}

// builtRun is what the relocation pass decided for one run.
type builtRun struct {
	// Moved is the group that moved, -1 for none.
	Moved int
	// Decisions has one entry per built group.
	Decisions []EmbedDecision
	// Price is the switching price of the move, in score units (0 for none and
	// for bad ground).
	Price float64
}

type relocateMove struct {
	group  int
	rect   Rectangle
	cost   float64
	margin float64
	bad    bool
}

// relocate is the relocation pass over a placed embedding: the single built
// room that gains most by moving moves, when any triggers. blocks is changed
// in place; the groups that were not built are not touched.
func (e *embedder) relocate(blocks []Rectangle) builtRun {
	out := builtRun{Moved: -1}
	var best *relocateMove
	below := map[int]bool{}
	trigger := map[int]relocateMove{}
	for i := range e.nRooms {
		h := e.home[i]
		if h.Width == 0 || e.pinned[i] || e.inFlight {
			continue
		}
		old := blocks[i]
		blocks[i] = Rectangle{}
		here := e.blockCost(i, h, blocks)
		sizes := [][2]int32{{h.Width, h.Height}}
		if h.Width != h.Height {
			sizes = append(sizes, [2]int32{h.Height, h.Width})
		}
		richHere := e.rich.sum(h)
		threshold := float64(switchThreshold(PlannedRoom{Region: RectRegion(h)}))
		var bestBad, bestGain *relocateMove
		var any bool
		var top float64
		for _, c := range e.core {
			for _, sh := range sizes {
				r := Rectangle{X: c.X, Z: c.Z, Width: sh[0], Height: sh[1]}
				if r == h || collides(r, h) || !e.fits(i, r, blocks, e.sky[i]) {
					continue
				}
				cost := e.blockCost(i, r, blocks)
				cand := &relocateMove{group: i, rect: r, cost: cost, margin: here - cost}
				if !any || cand.margin > top {
					any, top = true, cand.margin
				}
				if richHere > 0 && e.rich.sum(r) < richHere {
					cand.bad = true
					if bestBad == nil || better(cost, r, bestBad.cost, bestBad.rect, true) {
						bestBad = cand
					}
				}
				if cand.margin >= threshold && (bestGain == nil || better(cost, r, bestGain.cost, bestGain.rect, true)) {
					bestGain = cand
				}
			}
		}
		blocks[i] = old
		switch {
		case bestBad != nil:
			trigger[i] = *bestBad
		case bestGain != nil:
			trigger[i] = *bestGain
		case any && top > 0:
			below[i] = true
		}
	}
	for _, i := range slices.Sorted(maps.Keys(trigger)) {
		t := trigger[i]
		if best == nil || t.bad && !best.bad || t.bad == best.bad && t.margin > best.margin {
			best = &t
		}
	}
	for i := range e.nRooms {
		if e.home[i].Width == 0 {
			continue
		}
		d := EmbedDecision{Group: i, Other: -1, Verdict: EmbedKeep, Reason: ReasonEmbedBuiltKept, Room: e.homeRoom[i]}
		switch {
		case e.pinned[i]:
			d.Reason = ReasonEmbedBuiltPinned
		case e.inFlight:
			d.Reason = ReasonEmbedBuiltInFlight
		case best != nil && best.group == i:
			d.Verdict, d.Reason = EmbedMove, ReasonEmbedBuiltMovedGain
			if best.bad {
				d.Reason = ReasonEmbedBuiltBadGround
			} else {
				out.Price = float64(switchThreshold(PlannedRoom{Region: RectRegion(e.home[i])}))
			}
		case trigger[i].group == i && trigger[i].rect.Width > 0:
			d.Reason = ReasonEmbedBuiltDeferred
		case below[i]:
			d.Reason = ReasonEmbedBuiltBelow
		}
		out.Decisions = append(out.Decisions, d)
	}
	if best != nil {
		out.Moved = best.group
		blocks[best.group] = best.rect
	}
	return out
}
