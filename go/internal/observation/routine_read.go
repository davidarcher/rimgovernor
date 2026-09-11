package observation

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type RoutineSource interface {
	ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
}

type RoutineReading struct {
	ColonyReading
	Emergency        policy.EmergencyFacts
	EmergencyReceipt bridge.Result
	PawnReceipt      bridge.Result
}

type routineBracket struct {
	RoutineSource
	expected    Identity
	emergency   bridge.EmergencyObservation
	receipt     bridge.Result
	pawnReceipt bridge.Result
	armed       domain.Fact[int64]
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
	if complete, known := s.emergency.Facts.ColonistsComplete.Value(); known && complete {
		ids := make([]string, 0, len(s.emergency.Facts.Colonists))
		for _, pawn := range s.emergency.Facts.Colonists {
			ids = append(ids, string(pawn.ID))
		}
		if len(ids) > 0 {
			pawns, pawnReceipt, err := s.ReadCombatPawns(ctx, id, ids)
			s.pawnReceipt = pawnReceipt
			if err != nil {
				return nil, receipt, err
			}
			if pawns == nil || pawns.GetObserved() == nil {
				return nil, receipt, ErrContract
			}
			if err = bridge.ValidateCombatPawnSnapshot(pawns.GetObserved(), id, ids); err != nil {
				return nil, receipt, err
			}
			observed, err := contextIdentity(pawns.GetObserved().Context)
			if err != nil {
				return nil, receipt, err
			}
			observed.Paused = s.expected.Paused
			if !sameColonyBoundary(observed, s.expected) {
				return nil, receipt, ErrChanged
			}
			s.armed = routineArmed(colony.GetObserved(), s.emergency.Facts, pawns.GetObserved())
		}
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
	reading.Projection.Facts.Armed = bracket.armed
	return RoutineReading{ColonyReading: reading, Emergency: bracket.emergency.Facts, EmergencyReceipt: bracket.receipt, PawnReceipt: bracket.pawnReceipt}, nil
}
