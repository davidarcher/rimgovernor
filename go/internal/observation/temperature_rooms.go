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

func (s *routineBracket) readTemperature(ctx context.Context, id *c.Identity, colony *o.ColonyFactsReply) error {
	if !s.temperatureEnabled {
		return nil
	}
	source, ok := s.RoutineSource.(TemperatureSource)
	if !ok {
		return ErrContract
	}
	reply, receipt, err := source.ReadTemperatureRooms(ctx, id)
	s.temperatureReceipt = receipt
	if errors.Is(err, bridge.ErrUnavailable) {
		return nil
	}
	if err != nil {
		return err
	}
	if reply == nil || reply.GetObserved() == nil {
		return ErrContract
	}
	rooms := reply.GetObserved()
	if err := bridge.ValidateTemperatureRooms(rooms, id); err != nil {
		return err
	}
	observed, err := contextIdentity(rooms.Context)
	if err != nil {
		return err
	}
	observed.Paused = s.expected.Paused
	if !sameColonyBoundary(observed, s.expected) {
		return ErrChanged
	}
	s.temperature = temperatureRooms(rooms, colonySleeping(colony.GetObserved()))
	return nil
}

func temperatureRooms(rooms *o.RoomsSnapshot, sleeping domain.Fact[policy.SleepingObservation]) domain.Fact[policy.TemperatureObservation] {
	result := policy.TemperatureObservation{}
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
		row := policy.TemperatureRoom{ID: room.GetId(), Temperature: optional(room.TemperatureC), Enclosed: domain.Known(room.GetProperRoom() && !room.GetDoorway() && !room.GetOutdoors() && !room.GetPsychologicallyOutdoors() && !room.GetTouchesMapEdge() && room.GetOpenRoofCount() == 0)}
		for _, bed := range room.Beds {
			row.Beds = append(row.Beds, bed.Building.GetId())
		}
		contents := []policy.Amount{}
		for _, q := range room.Contents {
			contents = append(contents, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
		}
		row.Contents = domain.Known(contents)
		for _, cell := range room.Cells {
			row.Cells = append(row.Cells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		result.Rooms = append(result.Rooms, row)
	}
	return domain.Known(result)
}
