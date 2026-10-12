package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RoomTrip is the pawn traffic between two rooms of a plan: A and B are room
// ids (A below B) and Weight the trips a day, the station graph's flow
// between the two rooms' groups (GroupLink.Weight, summed). The trips are the
// edges of the room graph a plan without a spine is scored over.
type RoomTrip struct {
	A, B   int32
	Weight float64
}

// tripWalk is the flow-weighted walking distance over p's room trips: each
// trip pays Weight times the shortest walk (routeWalk: doors, link doors,
// hallways and the open ground round the rooms) between its rooms' centres.
// A room that cannot be reached costs walkUnreachable steps; a trip naming a
// room the plan lacks costs nothing. The total is rounded to whole steps.
func tripWalk(p LayoutPlan) int {
	walk, rooms := routeWalk(p)
	centre := map[int32]domain.Cell{}
	for _, r := range rooms {
		b := r.Region.Bounds()
		centre[r.ID] = domain.Cell{X: b.X + b.Width/2, Z: b.Z + b.Height/2}
	}
	// One search per target room serves every trip that ends there.
	byTarget := map[int32][]RoomTrip{}
	var targets []int32
	for _, t := range p.Trips {
		if _, ok := centre[t.A]; !ok {
			continue
		}
		if _, ok := centre[t.B]; !ok {
			continue
		}
		if _, seen := byTarget[t.B]; !seen {
			targets = append(targets, t.B)
		}
		byTarget[t.B] = append(byTarget[t.B], t)
	}
	total := 0.0
	for _, to := range targets {
		dist := routeDist(walk, []domain.Cell{centre[to]})
		for _, t := range byTarget[to] {
			d, ok := dist[centre[t.A]]
			if !ok {
				d = walkUnreachable
			}
			total += t.Weight * float64(d)
		}
	}
	return int(math.Round(total))
}

// OutsideTrip is the pawn traffic between a room and the outside (the graph's
// PurposeOutside node, which the tiler maps to the plan's Entrances, not to a
// room): Weight is the trips a day, one way, as RoomTrip.Weight is (the
// group's weight to the outside node). Only exteriorDoorMovements counts the
// way back.
type OutsideTrip struct {
	Room   int32
	Weight float64
}
