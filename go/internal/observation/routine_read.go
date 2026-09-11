package observation

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type RoutineSource interface {
	ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
}

type RoutineReading struct {
	ColonyReading
	Emergency        policy.EmergencyFacts
	EmergencyReceipt bridge.Result
}

type routineBracket struct {
	RoutineSource
	expected  Identity
	emergency bridge.EmergencyObservation
	receipt   bridge.Result
}

// Read the emergency census inside ObserveColony's identity brackets.
func (s *routineBracket) ReadColonyFacts(ctx context.Context, id *c.Identity, planning bool, defs []string) (*o.ColonyFactsReply, bridge.Result, error) {
	colony, receipt, err := s.RoutineSource.ReadColonyFacts(ctx, id, planning, defs)
	if err != nil {
		return colony, receipt, err
	}
	s.emergency, s.receipt, err = s.ReadEmergency(ctx, id)
	if err != nil {
		return nil, receipt, err
	}
	identity, err := contextIdentity(s.emergency.Context)
	if err != nil {
		return nil, receipt, err
	}
	identity.Paused = s.expected.Paused
	if !sameColonyBoundary(identity, s.expected) {
		return nil, receipt, ErrChanged
	}
	return colony, receipt, nil
}

func ObserveRoutine(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration) (RoutineReading, error) {
	if source == nil {
		return RoutineReading{}, ErrContract
	}
	bracket := &routineBracket{RoutineSource: source, expected: expected}
	reading, err := ObserveColony(ctx, bracket, clock, expected, maxAge, true, nil)
	if err != nil {
		return RoutineReading{}, err
	}
	return RoutineReading{ColonyReading: reading, Emergency: bracket.emergency.Facts, EmergencyReceipt: bracket.receipt}, nil
}
