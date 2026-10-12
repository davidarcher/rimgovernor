package policy

import (
	"slices"
	"sort"
)

// The tiler beside the replan (#3002, J1 of #2965). At a replan the station
// chain already reads the built rooms (Built, Pinned, the plan's RetiredGround);
// TileStations over ReplanTileFacts tiles them and DiffReplanTile says, room by
// room, what the tiled plan would do with the plan the old replan produced. It
// is telemetry: the old plan stays the output.

// Verdicts of a room in the replan diff.
const (
	// TileDiffSame: the tiled plan holds the room where the old plan has it.
	TileDiffSame = "same"
	// TileDiffMoved: the tiled plan holds it elsewhere, or as the successor of
	// a standing room.
	TileDiffMoved = "moved"
	// TileDiffNew: the tiled plan holds a room the old plan lacks.
	TileDiffNew = "new"
	// TileDiffDropped: the old plan holds a room the tiled plan lacks.
	TileDiffDropped = "dropped"
)

// Reasons of a room in the replan diff.
const (
	ReasonDiffKept         = "tile_kept_built"
	ReasonDiffSuccessor    = "tile_successor"
	ReasonDiffRetired      = "tile_built_retired_by_old"
	ReasonDiffSuperseded   = "tile_superseded"
	ReasonDiffNoNode       = "tile_no_node"
	ReasonDiffNotBlock     = "tile_not_a_block"
	ReasonDiffOutdoor      = "tile_outdoor"
	ReasonDiffNoNativeRole = "tile_no_native_role"
	ReasonDiffSited        = "tile_sited_again"
	// ReasonDiffUnpinned: a standing room the chain cannot pin, matched to the
	// room of its role the tiler builds of its own (a rebuild).
	ReasonDiffUnpinned  = "tile_unpinned_built"
	ReasonDiffNoOldRoom = "tile_no_old_room"
)

// TileRoomDiff is one room of the replan diff. Room is the old plan's room (0
// for a New one), Tiled the tiled plan's room (0 for a Dropped one; its ids are
// the tiler's own, a kept built room carries its old id).
type TileRoomDiff struct {
	Room, Tiled int32
	Role        PlannedRole
	Verdict     string
	Reason      string
	Built       bool
}

// UnmatchedBuilt is a standing room with no node in the tiled plan. Reason is
// ReasonDiffOutdoor or ReasonDiffNoNativeRole (BuiltRegions skips both unless
// the room is a room node's),
// ReasonDiffNotBlock (not a rectangle) or ReasonDiffNoNode (a native role whose
// group had no block for it).
type UnmatchedBuilt struct {
	Room   int32
	Role   PlannedRole
	Reason string
}

// ReplanTileDiff is the tiled plan against the old replan's plan.
type ReplanTileDiff struct {
	Rooms     []TileRoomDiff
	Unmatched []UnmatchedBuilt
}

// Count is the number of rooms with the verdict.
func (d ReplanTileDiff) Count(verdict string) int {
	n := 0
	for _, r := range d.Rooms {
		if r.Verdict == verdict {
			n++
		}
	}
	return n
}

// ReplanTileFacts are f, the facts of a replan review (the saved plan, its
// Built and Pinned rooms), as the tiler reads them: the ground a fresh plan is
// tiled on (the survey's zones with the core, less the geothermal enclosures'
// cells), as DeriveLayoutPlan lays it, in place of the saved plan's core-free
// zones; only the power sites' reservations and sites kept, because the
// perimeter, the herd and the yard sites are laid over the tiled plan after it
// (the overlays of DeriveLayoutPlan). f is not changed.
func ReplanTileFacts(f StationFacts, s MapSurvey, geysers []PowerGeyser) StationFacts {
	f.Plan.Zones = coreWithout(Zone(s), geothermalCells(geyserFootprints(geysers)))
	f.Plan.Reservations = slices.DeleteFunc(slices.Clone(f.Plan.Reservations), func(r LayoutReservation) bool { return !slices.Contains(powerSiteKinds, r.Kind) })
	f.Plan.Sites = slices.DeleteFunc(slices.Clone(f.Plan.Sites), func(p PlannedSite) bool { return perimeterSiteKinds[p.Kind] })
	f.Survey = s
	return f
}

// DiffReplanTile compares the tiled plan of advice with old, the plan the old
// replan produced from the plan whose standing rooms are built. A standing room
// the embedding kept or superseded is claimed through its block (Built,
// Replaces); the unbuilt rooms are matched to the blocks left over by role,
// the identical interior first and the nearest after. Attached rooms (airlocks,
// porches) are the tiler's own and not diffed.
func DiffReplanTile(old LayoutPlan, built map[int32]bool, advice StationAdvice) ReplanTileDiff {
	var tiled []PlannedRoom
	var blocks []EmbeddedBlock
	if advice.Tiled != nil {
		all := advice.Tiled.Plan.AllRooms()
		for _, b := range advice.Embedding.Blocks {
			id, ok := advice.Tiled.Rooms[b.Group]
			if !ok {
				continue
			}
			if i := slices.IndexFunc(all, func(r PlannedRoom) bool { return r.ID == id }); i >= 0 {
				blocks = append(blocks, b)
				tiled = append(tiled, all[i])
			}
		}
	}
	type claim struct {
		tiled     int
		successor bool
	}
	claims := map[int32]claim{}
	var free []int
	for i, b := range blocks {
		switch {
		case b.Built != 0:
			claims[b.Built] = claim{tiled: i}
		case b.Replaces != 0:
			claims[b.Replaces] = claim{tiled: i, successor: true}
		default:
			free = append(free, i)
		}
	}
	superseded := map[int32]bool{}
	for _, r := range old.AllRooms() {
		if built[r.ID] && r.Replaces != 0 {
			superseded[r.Replaces] = true
		}
	}
	notBlock := map[int32]bool{}
	for _, d := range advice.Embedding.Decisions {
		if d.Reason == ReasonEmbedBuiltNotRect {
			notBlock[d.Room] = true
		}
	}
	var out ReplanTileDiff
	var rooms []PlannedRoom
	for _, r := range old.AllRooms() {
		if !r.attached() {
			rooms = append(rooms, r)
		}
	}
	sort.Slice(rooms, func(i, j int) bool { return rooms[i].ID < rooms[j].ID })
	seen := map[int32]bool{}
	// pending are the rooms no block claims: the unbuilt rooms and the
	// standing ones the chain cannot pin (unmatched, with the reason).
	type pendingRoom struct {
		PlannedRoom
		unmatched string
	}
	var pending []pendingRoom
	for _, r := range rooms {
		seen[r.ID] = true
		if c, ok := claims[r.ID]; ok {
			row := TileRoomDiff{Room: r.ID, Tiled: tiled[c.tiled].ID, Role: r.Role, Built: built[r.ID], Verdict: TileDiffSame, Reason: ReasonDiffKept}
			if c.successor {
				row.Verdict, row.Reason = TileDiffMoved, ReasonDiffSuccessor
			} else if tiled[c.tiled].Region.Bounds() != r.Region.Bounds() {
				row.Verdict = TileDiffMoved
			}
			out.Rooms = append(out.Rooms, row)
			continue
		}
		if !built[r.ID] {
			pending = append(pending, pendingRoom{PlannedRoom: r})
			continue
		}
		if superseded[r.ID] {
			out.Rooms = append(out.Rooms, TileRoomDiff{Room: r.ID, Role: r.Role, Verdict: TileDiffDropped, Reason: ReasonDiffSuperseded, Built: true})
			continue
		}
		reason := unmatchedReason(r, notBlock[r.ID])
		out.Unmatched = append(out.Unmatched, UnmatchedBuilt{Room: r.ID, Role: r.Role, Reason: reason})
		pending = append(pending, pendingRoom{PlannedRoom: r, unmatched: reason})
	}
	// The pending rooms take the free blocks of their role: the same interior
	// first, then the nearest. A standing room takes one only as the room the
	// tiler would build of its own, since nothing pins it.
	taken := map[int]bool{}
	match := map[int32]int{}
	for pass := 0; pass < 2; pass++ {
		for _, r := range pending {
			if _, done := match[r.ID]; done {
				continue
			}
			best, bestDist := -1, 0.0
			for _, i := range free {
				if taken[i] || tiled[i].Role != r.Role {
					continue
				}
				if pass == 0 {
					if tiled[i].Region.Bounds() == r.Region.Bounds() {
						best = i
						break
					}
					continue
				}
				if d := rectCentreDistance(tiled[i].Region.Bounds(), r.Region.Bounds()); best < 0 || d < bestDist {
					best, bestDist = i, d
				}
			}
			if best >= 0 {
				taken[best], match[r.ID] = true, best
			}
		}
	}
	for _, r := range pending {
		i, ok := match[r.ID]
		row := TileRoomDiff{Room: r.ID, Role: r.Role, Built: r.unmatched != "", Verdict: TileDiffDropped, Reason: r.unmatched}
		if row.Reason == "" {
			row.Reason = ReasonDiffNoNode
		}
		if ok {
			row.Tiled, row.Verdict, row.Reason = tiled[i].ID, TileDiffMoved, ReasonDiffSited
			if tiled[i].Region.Bounds() == r.Region.Bounds() {
				row.Verdict = TileDiffSame
			}
			if r.unmatched != "" {
				row.Reason = ReasonDiffUnpinned
			}
		}
		out.Rooms = append(out.Rooms, row)
	}
	for i, b := range blocks {
		switch {
		case taken[i]:
		case b.Built != 0 && !seen[b.Built], b.Replaces != 0 && !seen[b.Replaces]:
			out.Rooms = append(out.Rooms, TileRoomDiff{Tiled: tiled[i].ID, Role: tiled[i].Role, Verdict: TileDiffNew, Reason: ReasonDiffRetired})
		case b.Built == 0 && b.Replaces == 0:
			out.Rooms = append(out.Rooms, TileRoomDiff{Tiled: tiled[i].ID, Role: tiled[i].Role, Verdict: TileDiffNew, Reason: ReasonDiffNoOldRoom})
		}
	}
	return out
}

// unmatchedReason is why a standing room has no block: the chain does not see
// outdoor rooms or rooms with no native role (BuiltRegions), a room that is no
// rectangle is no block, and any other had no block in its group.
func unmatchedReason(r PlannedRoom, notBlock bool) string {
	_, native := moduleRoomRoles[r.Role]
	_, special := matchedPlanned[r.Role]
	switch {
	case roomNodeRoles[r.Role]:
		// BuiltRegions pins it (a room node's room); it has no block only
		// because no node of its role stands in the graph or it is no rectangle.
		if notBlock {
			return ReasonDiffNotBlock
		}
		return ReasonDiffNoNode
	case r.Outdoor:
		return ReasonDiffOutdoor
	case !native && !special:
		return ReasonDiffNoNativeRole
	case notBlock:
		return ReasonDiffNotBlock
	}
	return ReasonDiffNoNode
}
