package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// PlannedNet is the conduit net the layout plan laid (LayoutPlan.Conduits): the
// cells of its links and the room interiors and site footprints the links end
// in. SelectPowerMethod routes a consumer along it, so a connect proposal's
// cells are the planned path (the built ones skipped, as ever) and not a path
// of its own. The zero value is no net: the router then searches the ground.
type PlannedNet struct {
	Cells map[domain.Cell]bool
	Pads  []Region
}

// PlannedNetOf is the net the plan holds.
func PlannedNetOf(plan LayoutPlan) PlannedNet {
	var n PlannedNet
	for _, l := range plan.Conduits {
		for _, c := range l.Cells {
			if n.Cells == nil {
				n.Cells = map[domain.Cell]bool{}
			}
			n.Cells[c] = true
		}
	}
	if len(n.Cells) == 0 {
		return PlannedNet{}
	}
	for _, r := range plan.AllRooms() {
		n.Pads = append(n.Pads, r.Region)
	}
	for _, s := range plan.Sites {
		n.Pads = append(n.Pads, s.Footprint)
	}
	for _, r := range plan.Reservations {
		n.Pads = append(n.Pads, r.Area)
	}
	return n
}

// route is the shortest path from start to a destination over the net's cells
// and the pads that hold start or a destination, within the cells route allows
// (terrain that takes a conduit, protected cells out). Destination first, as
// powerRoute. Nil when there is no net or it does not join the two.
func (n PlannedNet) route(start domain.Cell, destinations, allow map[domain.Cell]bool) []domain.Cell {
	if len(n.Cells) == 0 {
		return nil
	}
	allowed := map[domain.Cell]bool{}
	for c := range n.Cells {
		allowed[c] = allow[c]
	}
	for _, pad := range n.Pads {
		holds := pad.Contains(start)
		for d := range destinations {
			holds = holds || pad.Contains(d)
		}
		if !holds {
			continue
		}
		for _, c := range pad.Cells() {
			allowed[c] = allow[c]
		}
	}
	return powerRoute(start, destinations, allowed)
}

// plannedRoute is the planned path from start to a destination, or the
// shortest over the ground when the plan holds none that joins them (a
// consumer the plan never routed, a net cell the terrain refuses).
func plannedRoute(net PlannedNet, start domain.Cell, destinations, allow map[domain.Cell]bool) []domain.Cell {
	if path := net.route(start, destinations, allow); len(path) > 0 {
		return path
	}
	return powerRoute(start, destinations, allow)
}
