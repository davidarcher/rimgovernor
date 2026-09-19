package observation

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type TemperatureSource interface {
	ReadTemperatureRooms(context.Context, *c.Identity) (*o.ListRoomsReply, bridge.Result, error)
}

// readTemperature reads the typed room census when rooms are enabled and
// returns the validated snapshot; the caller projects it once the colony
// reply (whose beds decide eligibility) has arrived on its own lane. A nil
// snapshot with a nil error is a source without the census or one that
// reports it unavailable: the fact stays unknown.
func (s *routineBracket) readTemperature(ctx context.Context, id *c.Identity) (*o.RoomsSnapshot, error) {
	if !s.roomsEnabled {
		return nil, nil
	}
	source, ok := s.RoutineSource.(TemperatureSource)
	if !ok {
		return nil, ErrContract
	}
	reply, receipt, err := source.ReadTemperatureRooms(ctx, id)
	s.temperatureReceipt = receipt
	if errors.Is(err, bridge.ErrUnavailable) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	if reply == nil || reply.GetObserved() == nil {
		return nil, ErrContract
	}
	rooms := reply.GetObserved()
	if err := bridge.ValidateTemperatureRooms(rooms, id); err != nil {
		return nil, err
	}
	observed, err := contextIdentity(rooms.Context)
	if err != nil {
		return nil, err
	}
	observed.Paused = s.expected.Paused
	if !sameColonyBoundary(observed, s.expected) {
		return nil, ErrChanged
	}
	return rooms, nil
}

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
		result.Rooms = append(result.Rooms, row)
	}
	return domain.Known(result)
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
