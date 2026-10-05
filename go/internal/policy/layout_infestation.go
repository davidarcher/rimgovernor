package policy

import (
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Infestation prevention (#1067, epic #845). Insects dig out of open
// ground under overhead mountain that is dark: a small unused pocket of
// such ground beside the base is walled solid, and every planned room
// gets lamps lighting its whole floor above the darkness threshold.

// ReservePocketWall marks open overhead-mountain cells near the base the
// perimeter tier fills with wall.
const ReservePocketWall ReservationKind = "pocket_wall"

const (
	// pocketMaxCells is the largest pocket walled solid; a bigger open
	// cave is a room or the infestation response's business.
	pocketMaxCells = 20
	// pocketReach is how far outside the perimeter ring a pocket
	// still counts as beside the base.
	pocketReach int32 = 6
	// lampReach is the distance (cells) a standing lamp keeps ground glow
	// above the 0.3 darkness line infestations need.
	lampReach = 6.0
	// TierPocketPrefix and TierLightPrefix name the pocket-wall and
	// room-lamp sections under the perimeter's prefix, so the perimeter
	// recut builds and removes them with the wall.
	TierPocketPrefix = TierPerimeterPrefix + "pocket-"
	TierLightPrefix  = TierPerimeterPrefix + "light-"
)

// PlanMountainPockets replaces plan's pocket-wall reservations with one
// per row run of each small pocket: a 4-connected patch of at most
// pocketMaxCells walkable, unbuilt cells under thick roof, touching the
// perimeter ring or within pocketReach of it, that no room, hallway or
// other reservation uses. A plan without a perimeter holds none.
func PlanMountainPockets(plan LayoutPlan, s MapSurvey) LayoutPlan {
	var kept []LayoutReservation
	var ring Rectangle
	used := map[domain.Cell]bool{}
	for _, r := range plan.Reservations {
		if r.Kind == ReservePocketWall {
			continue
		}
		kept = append(kept, r)
		if r.Kind == ReservePerimeter {
			ring = unionRect(ring, r.Area)
		}
		for _, c := range rectCells(r.Area) {
			used[c] = true
		}
	}
	plan.Reservations = kept
	if ring.Width == 0 {
		return plan
	}
	for _, r := range plan.AllRooms() {
		for _, c := range rectCells(roomWalls(r)) {
			used[c] = true
		}
	}
	for _, sg := range plan.Hallways() {
		for _, c := range rectCells(pad(rectOf(sg.From, sg.To), SpineWidth/2)) {
			used[c] = true
		}
	}
	wi, _ := planInterior(plan, pocketReach)
	near := wi.near(pocketReach)
	open := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.ThickRoof && c.Walkable && !c.Rock && !c.Built {
			open[c.Cell] = true
		}
	}
	seen := map[domain.Cell]bool{}
	var starts []domain.Cell
	for c := range open {
		starts = append(starts, c)
	}
	sort.Slice(starts, func(i, j int) bool { return cellLess(starts[i], starts[j]) })
	for _, start := range starts {
		if seen[start] {
			continue
		}
		patch := []domain.Cell{start}
		seen[start] = true
		for i := 0; i < len(patch); i++ {
			c := patch[i]
			for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
				if open[n] && !seen[n] {
					seen[n] = true
					patch = append(patch, n)
				}
			}
		}
		if len(patch) > pocketMaxCells {
			continue
		}
		beside, free := false, true
		for _, c := range patch {
			beside = beside || near(c) >= 0
			free = free && !used[c]
		}
		if !beside || !free {
			continue
		}
		plan.Reservations = append(plan.Reservations, pocketRuns(patch)...)
	}
	return plan
}

// pocketRuns covers cells with 1-high row runs, rows then X ascending.
func pocketRuns(cells []domain.Cell) []LayoutReservation {
	sorted := append([]domain.Cell(nil), cells...)
	sort.Slice(sorted, func(i, j int) bool {
		return sorted[i].Z < sorted[j].Z || sorted[i].Z == sorted[j].Z && sorted[i].X < sorted[j].X
	})
	var out []LayoutReservation
	for i := 0; i < len(sorted); {
		j := i + 1
		for j < len(sorted) && sorted[j].Z == sorted[i].Z && sorted[j].X == sorted[j-1].X+1 {
			j++
		}
		out = append(out, LayoutReservation{Kind: ReservePocketWall, Area: Rectangle{X: sorted[i].X, Z: sorted[i].Z, Width: int32(j - i), Height: 1}})
		i = j
	}
	return out
}

// PocketSections is one section per pocket-wall reservation, each cell a
// wall with its stuff left for admission to fill.
func PocketSections(plan LayoutPlan, wall string) ([]PerimeterSection, error) {
	var out []PerimeterSection
	for _, r := range plan.Reservations {
		if r.Kind != ReservePocketWall {
			continue
		}
		s := PerimeterSection{Name: DefenseTierName(fmt.Sprintf("%s%02d", TierPocketPrefix, len(out)))}
		for _, c := range rectCells(r.Area) {
			b, err := domain.NewBuilding(wall, c, domain.North, "")
			if err != nil {
				return nil, err
			}
			s.Buildings = append(s.Buildings, b)
		}
		out = append(out, s)
	}
	return out, nil
}

// BaseRoomLamps is the lamp cells lighting every built room of plan (all
// but reserve rooms): a grid over each interior whose lamps sit no farther
// than lampReach from any floor cell. Rooms keep plan order.
func BaseRoomLamps(plan LayoutPlan) []domain.Cell {
	var out []domain.Cell
	for _, r := range plan.AllRooms() {
		if r.Role == PlannedReserve {
			continue
		}
		in := r.Interior
		// A lamp covers a square of side lampReach*sqrt2 around it.
		span := int32(math.Floor(lampReach * math.Sqrt2))
		nx, nz := (in.Width+span-1)/span, (in.Height+span-1)/span
		for iz := int32(0); iz < nz; iz++ {
			for ix := int32(0); ix < nx; ix++ {
				out = append(out, domain.Cell{X: in.X + (2*ix+1)*in.Width/(2*nx), Z: in.Z + (2*iz+1)*in.Height/(2*nz)})
			}
		}
	}
	return out
}

// LightSections is one section holding every lamp placed by
// BaseRoomLamps, in plan order.
func LightSections(plan LayoutPlan, lamp string) ([]PerimeterSection, error) {
	cells := BaseRoomLamps(plan)
	if len(cells) == 0 {
		return nil, nil
	}
	s := PerimeterSection{Name: DefenseTierName(TierLightPrefix + "00")}
	for _, c := range cells {
		b, err := domain.NewBuilding(lamp, c, domain.North, "")
		if err != nil {
			return nil, err
		}
		s.Buildings = append(s.Buildings, b)
	}
	return []PerimeterSection{s}, nil
}
