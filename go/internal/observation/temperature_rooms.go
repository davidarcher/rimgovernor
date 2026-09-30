package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func temperatureRooms(rooms *o.RoomsSnapshot, sleeping domain.Fact[policy.SleepingObservation]) domain.Fact[policy.RoomObservation] {
	result := policy.RoomObservation{}
	if known, ok := sleeping.Value(); ok {
		eligible := []string{}
		complete := true
		for _, bed := range known.Beds {
			human, hk := bed.Humanlike.Value()
			medical, mk := bed.Medical.Value()
			prisoners, pk := bed.Prisoners.Value()
			if !hk || !mk || !pk {
				complete = false
				break
			}
			if human && !medical && !prisoners {
				eligible = append(eligible, bed.ID)
			}
		}
		if complete {
			result.EligibleBeds = domain.Known(eligible)
		}
	}
	for _, room := range rooms.Rooms {
		row := policy.Room{ID: room.GetId(), Role: roomRole(room), Temperature: optional(room.TemperatureC), Enclosed: domain.Known(room.GetProperRoom() && !room.GetDoorway() && !room.GetOutdoors() && !room.GetPsychologicallyOutdoors() && !room.GetTouchesMapEdge() && room.GetOpenRoofCount() == 0)}
		for _, bed := range room.Beds {
			row.Beds = append(row.Beds, bed.Building.GetId())
		}
		contents := []policy.Amount{}
		for _, q := range room.Contents {
			contents = append(contents, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
		}
		row.Contents = domain.Known(contents)
		row.Cleanliness = roomStat(room, "Cleanliness")
		for _, cell := range room.Cells {
			row.Cells = append(row.Cells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		row.Roofed = domain.Unknown[bool]()
		if room.OpenRoofCount != nil {
			row.Roofed = domain.Known(room.GetOpenRoofCount() == 0)
		}
		for _, door := range room.Doors {
			row.Doors = append(row.Doors, policy.RoomDoor{Cell: domain.Cell{X: door.GetCell().GetX(), Z: door.GetCell().GetZ()}, Outside: domain.Cell{X: door.GetOutside().GetX(), Z: door.GetOutside().GetZ()}, Outdoors: optional(door.Outdoors)})
		}
		result.Rooms = append(result.Rooms, row)
	}
	return domain.Known(policy.MarkEnemyDoors(result, nil))
}

// roomStat reads one named native room stat; a missing or unavailable stat
// is unknown, never zero.
func roomStat(room *o.RoomState, name string) domain.Fact[float64] {
	for _, stat := range room.Stats {
		if stat.GetDefName() != name {
			continue
		}
		if stat.Unavailable != nil || stat.Value == nil {
			return domain.Unknown[float64]()
		}
		return domain.Known(stat.GetValue())
	}
	return domain.Unknown[float64]()
}

// The native census names Room.Role by RoomRoleDef defName; a missing role is
// an unknown fact, never a generic room.
func roomRole(room *o.RoomState) domain.Fact[policy.RoomRole] {
	if room.Role == nil {
		return domain.Unknown[policy.RoomRole]()
	}
	return domain.Known(policy.RoomRole(room.GetRole()))
}
