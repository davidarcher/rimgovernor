package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Tiler airlocks (#2916). On a bad-climate map (the roofed set, layout_roof.go)
// a room's exterior door opens a roofed room onto the outdoors. The door is
// priced two ways in sustained watts (climate.go): left as a door the pawns
// use, open for the share of the time their trips hold it, or as an airlock
// (attachAirlock), whose second door and two walls are a one-time cost counted
// at the layout's own rate (BuildW) and whose two doors are never open at
// once. The airlock is placed when it is the cheaper total, no veto either way;
// the ground it needs (the vestibule, its two walls and the outer door, all
// open ground no other airlock holds) is the only thing that can leave a
// priced airlock out, and the decision says so.
//
// Door traffic is the plan's own trips: each trip is walked over the plan
// (routeWalk) between its rooms' centres and charges its movements a day to
// every exterior door on the way, so a room pawns cross to reach the outdoor
// roles, or a circulation room the sleepers use, carries real traffic and a
// closet at the end of a corridor carries none. DoorOpenFraction turns
// movements into the fraction of time open. The airlock is advice beside the
// old planner, which stays authoritative until #2931.

// exteriorDoorMovements are the movements a day through each room's exterior
// door (its Door, when no link door stands there): every trip charges its
// weight to each exterior door on its shortest walk. A trip the plan cannot
// walk charges nothing.
func exteriorDoorMovements(p LayoutPlan) map[int32]float64 {
	owner := map[domain.Cell]int32{}
	links := map[domain.Cell]bool{}
	for _, l := range p.Links {
		links[l.Cell] = true
	}
	for _, r := range p.AllRooms() {
		if !r.Unfenced() && !links[r.Door] {
			owner[r.Door] = r.ID
		}
	}
	out := map[int32]float64{}
	if len(owner) == 0 || len(p.Trips) == 0 {
		return out
	}
	walk, rooms := routeWalk(p)
	centre := map[int32]domain.Cell{}
	for _, r := range rooms {
		b := r.Region.Bounds()
		centre[r.ID] = domain.Cell{X: b.X + b.Width/2, Z: b.Z + b.Height/2}
	}
	bySource := map[int32][]RoomTrip{}
	var sources []int32
	for _, t := range p.Trips {
		_, a := centre[t.A]
		_, b := centre[t.B]
		if !a || !b {
			continue
		}
		if _, seen := bySource[t.A]; !seen {
			sources = append(sources, t.A)
		}
		bySource[t.A] = append(bySource[t.A], t)
	}
	slices.Sort(sources)
	for _, from := range sources {
		// Breadth-first from the source, each cell remembering where it came
		// from, in a fixed neighbour order so the path is deterministic.
		parent := map[domain.Cell]domain.Cell{centre[from]: centre[from]}
		queue := []domain.Cell{centre[from]}
		for h := 0; h < len(queue); h++ {
			c := queue[h]
			for _, d := range [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
				n := domain.Cell{X: c.X + d[0], Z: c.Z + d[1]}
				if _, ok := walk[n]; !ok {
					continue
				}
				if _, seen := parent[n]; !seen {
					parent[n] = c
					queue = append(queue, n)
				}
			}
		}
		for _, t := range bySource[from] {
			at := centre[t.B]
			if _, ok := parent[at]; !ok {
				continue
			}
			for {
				if id, ok := owner[at]; ok {
					out[id] += t.Weight
				}
				next := parent[at]
				if next == at {
					break
				}
				at = next
			}
		}
	}
	return out
}

// placeAirlocks attaches an airlock to every roofed room's exterior door when
// the airlock is the cheaper opening in sustained watts, and reports whether
// it placed any. The airlocks follow the group rooms in plan order, each a
// child room (Parent set) with the next room id.
func (t *tiler) placeAirlocks() bool {
	p := t.out.Plan
	if p.Roofed.Empty() {
		t.noteAirlock(-1, ReasonTileAirlockTemperate, TilePlace, 0, 0)
		return false
	}
	curve, known := t.in.OutdoorCurve.Value()
	m := t.in.Catalog.Climate
	if !known || !m.Valid() || !finitePositive(t.in.SteelValue) {
		t.noteAirlock(-1, ReasonTileAirlockUnpriced, TileUnsatisfied, 0, 0)
		return false
	}
	movements := exteriorDoorMovements(p)
	claimed := map[domain.Cell]bool{}
	placed := false
	for i := range t.rooms {
		room := p.Rooms[i]
		if t.extDoor[i] == nil || room.Unfenced() || !roofedRoom(p.Roofed, room) {
			continue
		}
		g := t.in.Groups[t.rooms[i].group]
		band := ComfortBand()
		if groupCold(g) && t.in.FullRotRateC > 0 {
			band = Band{MinC: math.Inf(-1), MaxC: t.in.FullRotRateC}
		}
		options, err := ClimateOptions(m, DefaultSiteTypeWeights(), t.in.SteelValue, curve, ClimateRoom{Cells: room.Region.Area(), Band: band},
			ClimateSide{Outdoors: true}, DoorOpenFraction(movements[room.ID]), []Opening{OpeningDoor, OpeningAirlock})
		if err != nil {
			t.noteAirlock(g.ID, ReasonTileAirlockUnpriced, TileUnsatisfied, 0, 0)
			continue
		}
		door, airlock := options[0].TotalW, options[1].TotalW
		if airlock >= door {
			t.noteAirlock(g.ID, ReasonTileAirlockNotPaid, TileUnsatisfied, door, airlock)
			continue
		}
		child := attachAirlock(room.Door, room.DoorRot)
		ground := append(child.AirlockWalls(), child.Vestibule(), child.Door)
		if slices.ContainsFunc(ground, func(c domain.Cell) bool { return !t.free[c] || claimed[c] }) || !t.free[outside(child.Door, room.DoorRot)] {
			t.noteAirlock(g.ID, ReasonTileAirlockNoGround, TileUnsatisfied, door, airlock)
			continue
		}
		for _, c := range ground {
			claimed[c] = true
		}
		child.ID, child.Parent = t.out.Plan.NextRoomID, room.ID
		t.out.Plan.NextRoomID++
		t.out.Plan.Rooms = append(t.out.Plan.Rooms, child)
		t.noteAirlock(g.ID, ReasonTileAirlock, TilePlace, door, airlock)
		placed = true
	}
	return placed
}

// roofedRoom reports every floor cell of r in the roofed set.
func roofedRoom(roofed Region, r PlannedRoom) bool {
	return !slices.ContainsFunc(r.Region.Cells(), func(c domain.Cell) bool { return !roofed.Contains(c) })
}

func (t *tiler) noteAirlock(group int, reason, verdict string, doorW, airlockW float64) {
	t.out.Decisions = append(t.out.Decisions, TileDecision{Group: group, Other: -1, Verdict: verdict, Reason: reason, DoorW: doorW, AirlockW: airlockW})
}
