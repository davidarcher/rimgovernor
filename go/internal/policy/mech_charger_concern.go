package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// EnsureMechCharger keeps a recharging station free for the mechs (epic
// #1667, #1688). A mechanitor's mechs recharge one at a time on a
// Building_MechCharger (MechChargerOwed): the goal is in deficit while the
// colony owes one more charger, and settles once a charger stands idle. It
// is a Standard whose target is no outstanding work, like ManagePollution.
// The method is one charger construction; powering it is the power goal's,
// emptying its waste is ManagePollution's.
//
// The charger is a building that produces wastepacks, so it is sited by the
// polluting-machine rule (PollutionSites, #1684) over free ground, never by a
// definition name: the catalog's mech_charger flag finds the definition.
const EnsureMechCharger ConcernID = "EnsureMechCharger"

// MechChargerNeed is RoundsFacts.MechChargerOwed's value: whether the colony
// owes one more charger, unknown while the mechs or the charger list are
// unread.
func MechChargerNeed(fleet domain.Fact[MechFleet], chargers domain.Fact[[]MechCharger]) domain.Fact[bool] {
	f, fleetKnown := fleet.Value()
	if _, read := chargers.Value(); !fleetKnown || !read {
		return domain.Unknown[bool]()
	}
	return domain.Known(MechChargerOwed(len(f.Mechanitors), chargers))
}

// MechChargerSiteFacts is what siting a charger reads from the planning
// window: its cells, the zone ids that are growing fields, the room census
// and the wastepack disposal (atomizer) cells.
type MechChargerSiteFacts struct {
	Bounds     Bounds
	Cells      []SiteCell
	FieldZones map[string]bool
	Rooms      domain.Fact[RoomObservation]
	Disposal   []domain.Cell
}

// MechChargerSites ranks every width x height footprint lying wholly on
// known free ground (walkable, unoccupied, covered by no zone, not a
// doorway) by PollutionSites: far from field cells, bedroom and barracks
// cells, dining and recreation room cells and polluted cells, then near the
// disposal. An unknown room census leaves the room avoid sets empty; an
// unknown cell verdict is not free ground.
func MechChargerSites(f MechChargerSiteFacts, width, height int32) ([]PollutionSite, error) {
	free := map[domain.Cell]bool{}
	req := PollutionSiteRequest{Bounds: f.Bounds, Disposal: f.Disposal}
	for _, c := range f.Cells {
		zone, zk := c.Zone.Value()
		if id, known := c.ZoneID.Value(); zk && zone && known && f.FieldZones[id] {
			req.FieldCells = append(req.FieldCells, c.Cell)
		}
		if polluted, ok := c.Polluted.Value(); ok && polluted {
			req.PollutedCells = append(req.PollutedCells, c.Cell)
		}
		walkable, wk := c.Walkable.Value()
		occupied, ok := c.Occupied.Value()
		door, dk := c.Doorway.Value()
		free[c.Cell] = wk && walkable && ok && !occupied && zk && !zone && dk && !door
	}
	if rooms, known := f.Rooms.Value(); known {
		for _, room := range rooms.Rooms {
			role, ok := room.Role.Value()
			switch {
			case !ok:
			case role == RoomRoleBedroom || role == RoomRoleBarracks:
				req.BedroomCells = append(req.BedroomCells, room.Cells...)
			case role == RoomRoleDiningRoom || role == RoomRoleRecRoom:
				req.LivingCells = append(req.LivingCells, room.Cells...)
			}
		}
	}
	corners := make([]domain.Cell, 0, len(free))
	for c, ok := range free {
		if ok {
			corners = append(corners, c)
		}
	}
	sort.Slice(corners, func(i, j int) bool { return cellLess(corners[i], corners[j]) })
	for _, c := range corners {
		r := Rectangle{X: c.X, Z: c.Z, Width: width, Height: height}
		if r.X+r.Width > f.Bounds.Width || r.Z+r.Height > f.Bounds.Height {
			continue
		}
		whole := true
		for _, cell := range rectCells(r) {
			whole = whole && free[cell]
		}
		if whole {
			req.Candidates = append(req.Candidates, r)
		}
	}
	return PollutionSites(req)
}
