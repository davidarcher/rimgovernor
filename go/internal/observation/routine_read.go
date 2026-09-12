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
	ReadRoutinePawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
}

type RoutineReading struct {
	ColonyReading
	Emergency          policy.EmergencyFacts
	EmergencyReceipt   bridge.Result
	PawnReceipt        bridge.Result
	DefinitionReceipt  bridge.Result
	TemperatureReceipt bridge.Result
}

type routineBracket struct {
	temperatureEnabled bool
	temperature        domain.Fact[policy.TemperatureObservation]
	temperatureReceipt bridge.Result
	claims             domain.Fact[[]policy.ConstructionClaim]
	construction       domain.Fact[policy.CurrentConstruction]
	RoutineSource
	expected          Identity
	emergency         bridge.EmergencyObservation
	receipt           bridge.Result
	pawnReceipt       bridge.Result
	armed             domain.Fact[int64]
	work              domain.Fact[[]policy.WorkPawn]
	medical           domain.Fact[[]policy.CarePawn]
	definitions       []string
	extraDefinitions  []PlanningDefinition
	definitionReceipt bridge.Result
}

// Read the emergency census inside ObserveColony's identity brackets.
func (s *routineBracket) ReadColonyFacts(ctx context.Context, id *c.Identity, planning bool, defs []string) (*o.ColonyFactsReply, bridge.Result, error) {
	colony, receipt, err := s.RoutineSource.ReadColonyFacts(ctx, id, planning, defs)
	if err != nil {
		return colony, receipt, err
	}
	if err := s.readConstruction(ctx, id); err != nil {
		return nil, receipt, err
	}
	if err := s.readProjectDefinitions(ctx, id, colony); err != nil {
		return nil, receipt, err
	}
	if err := s.readTemperature(ctx, id, colony); err != nil {
		return nil, receipt, err
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
			pawns, pawnReceipt, err := s.ReadRoutinePawns(ctx, id, ids)
			s.pawnReceipt = pawnReceipt
			if err != nil {
				return nil, receipt, err
			}
			if pawns == nil || pawns.GetObserved() == nil {
				return nil, receipt, ErrContract
			}
			if err = bridge.ValidateRoutinePawnSnapshot(pawns.GetObserved(), id, ids); err != nil {
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
			s.work = routineWork(colony.GetObserved(), s.emergency.Facts, pawns.GetObserved())
			s.medical = routineMedical(colony.GetObserved(), s.emergency.Facts, pawns.GetObserved())
		}
	}
	return colony, receipt, nil
}

func ObserveRoutine(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, definitions ...string) (RoutineReading, error) {
	return ObserveRoutineOwned(ctx, source, clock, expected, maxAge, domain.Unknown[[]policy.ConstructionClaim](), definitions...)
}
func ObserveRoutineOwned(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (RoutineReading, error) {
	return observeRoutine(ctx, source, clock, expected, maxAge, claims, false, definitions...)
}

func ObserveRoutineTemperature(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (RoutineReading, error) {
	return observeRoutine(ctx, source, clock, expected, maxAge, claims, true, definitions...)
}

func observeRoutine(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], temperature bool, definitions ...string) (RoutineReading, error) {
	if source == nil {
		return RoutineReading{}, ErrContract
	}
	bracket := &routineBracket{temperatureEnabled: temperature, claims: claims, RoutineSource: source, expected: expected, definitions: append([]string(nil), definitions...)}
	reading, err := ObserveColony(ctx, bracket, clock, expected, maxAge, true, nil)
	if err != nil {
		return RoutineReading{}, err
	}
	reading.Projection.Facts.CurrentConstruction = bracket.construction
	reading.Projection.Facts.Armed = bracket.armed
	reading.Projection.WorkPawns = bracket.work
	reading.Projection.Facts.MedicalPawns = bracket.medical
	reading.Projection.Facts.Gear = routineGear(reading.Projection.Facts.Gear, bracket.emergency.Facts)
	reading.Projection.Definitions = append(reading.Projection.Definitions, bracket.extraDefinitions...)
	if temperature {
		reading.Projection.TemperaturePlanning = bracket.temperature
		reading.Projection.Facts.SleepingMin, reading.Projection.Facts.SleepingMax = policy.TemperatureRange(bracket.temperature)
	}
	return RoutineReading{ColonyReading: reading, Emergency: bracket.emergency.Facts, EmergencyReceipt: bracket.receipt, PawnReceipt: bracket.pawnReceipt, DefinitionReceipt: bracket.definitionReceipt, TemperatureReceipt: bracket.temperatureReceipt}, nil
}

// Request only project definitions absent from the default planning census. Both
// reads stay inside the same paused identity and freshness bracket; crop inputs
// from the default census are retained.
func (s *routineBracket) readProjectDefinitions(ctx context.Context, id *c.Identity, colony *o.ColonyFactsReply) error {
	if len(s.definitions) == 0 {
		return nil
	}
	if len(s.definitions) > 256 {
		return ErrContract
	}
	base, err := DecodeColony(colony, s.expected)
	if err != nil {
		return err
	}
	seen := map[string]bool{}
	for _, d := range base.Definitions {
		seen[d.Name] = true
	}
	missing := []string{}
	for _, name := range s.definitions {
		if !seen[name] {
			missing = append(missing, name)
			seen[name] = true
		}
	}
	if len(missing) == 0 {
		return nil
	}
	reply, receipt, err := s.RoutineSource.ReadColonyFacts(ctx, id, true, missing)
	s.definitionReceipt = receipt
	if err != nil {
		return err
	}
	extra, err := DecodeColony(reply, s.expected)
	if err != nil {
		return err
	}
	extra.Identity.Paused = s.expected.Paused
	if !sameColonyBoundary(extra.Identity, s.expected) {
		return ErrChanged
	}
	wanted := map[string]bool{}
	for _, name := range missing {
		wanted[name] = true
	}
	for _, d := range extra.Definitions {
		if !wanted[d.Name] {
			return ErrContract
		}
		s.extraDefinitions = append(s.extraDefinitions, d)
	}
	return nil
}
