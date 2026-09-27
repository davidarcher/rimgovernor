package observation

import (
	"context"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// RoutineSource is the routine census: one decoded frame per reading
// (bridge.Client.ReadRoutineFrame), plus the colony facts read for
// definitions the frame's default catalog lacks.
type RoutineSource interface {
	ColonySource
	ReadRoutineFrame(context.Context, *c.Identity) (bridge.RoutineFrame, error)
}

type RoutineReading struct {
	ColonyReading
	Emergency policy.EmergencyFacts
	// Sections is the reading's decoded census, per section with the tick
	// each reply described, for the step's facts.Store.
	Sections RoutineSections
	// Frame is the frame the reading decoded.
	Frame bridge.RoutineFrame
}

func ObserveRoutineOwned(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (RoutineReading, error) {
	return observeRoutine(ctx, source, clock, expected, maxAge, claims, false, definitions...)
}

// ObserveRoutineRooms additionally reads the typed room census. Temperature
// planning takes room heat from it and comfort takes each facility's
// hosting room role: without the census no comfort facility can be
// certified as hosted, so comfort becomes unknown.
func ObserveRoutineRooms(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (RoutineReading, error) {
	return observeRoutine(ctx, source, clock, expected, maxAge, claims, true, definitions...)
}

// frameColony answers ObserveColony's colony facts and zone reads from the
// frame.
type frameColony struct {
	RoutineSource
	frame bridge.RoutineFrame
}

func (f frameColony) ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error) {
	return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: f.frame.Colony}}, bridge.Result{}, nil
}

func (f frameColony) ReadZoneSection(context.Context, *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	if f.frame.Zones == nil {
		return bridge.ZonesRead{}, bridge.Result{}, bridge.ErrUnavailable
	}
	return *f.frame.Zones, bridge.Result{}, nil
}

// observeRoutine decodes one frame. ObserveColony checks the frame's colony
// context against expected; every other section of the frame shares that
// context and tick, so none is checked against another (#306, #884).
func observeRoutine(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], rooms bool, definitions ...string) (RoutineReading, error) {
	if source == nil {
		return RoutineReading{}, ErrContract
	}
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	started := clock.Now()
	frame, err := source.ReadRoutineFrame(ctx, id)
	if err != nil {
		return RoutineReading{}, err
	}
	if frame.Colony == nil || frame.Emergency.Context == nil {
		return RoutineReading{}, ErrContract
	}
	reading, err := ObserveColony(ctx, frameColony{source, frame}, clock, expected, maxAge, true, nil)
	if err != nil {
		return RoutineReading{}, err
	}
	p := &reading.Projection
	colony, emergency := frame.Colony, frame.Emergency.Facts
	extra, err := readProjectDefinitions(ctx, source, id, expected, p.Definitions, definitions)
	if err != nil {
		return RoutineReading{}, err
	}
	p.Definitions = append(p.Definitions, extra...)
	p.Facts.CurrentConstruction = domain.Unknown[policy.CurrentConstruction]()
	if frame.Construction != nil {
		if err := bridge.ValidateConstructionBuildings(frame.Construction, id, nil); err != nil {
			return RoutineReading{}, err
		}
		if p.Facts.CurrentConstruction, err = ConstructionBuildings(frame.Construction, nil); err != nil {
			return RoutineReading{}, err
		}
	}
	pawns, err := routinePawns(frame, id)
	if err != nil {
		return RoutineReading{}, err
	}
	p.Facts.Armed, p.WorkPawns, p.Facts.MedicalPawns, p.Facts.MoodPawns = domain.Fact[int64]{}, domain.Fact[[]policy.WorkPawn]{}, domain.Fact[[]policy.CarePawn]{}, domain.Fact[[]policy.MoodPawn]{}
	if pawns != nil {
		p.Facts.Armed = routineArmed(colony, emergency, pawns)
		p.WorkPawns = routineWork(colony, emergency, pawns)
		p.Facts.MedicalPawns = routineMedical(colony, emergency, pawns)
		p.Facts.MoodPawns = routineMood(colony, emergency, pawns)
	} else if complete, known := emergency.ColonistsComplete.Value(); known && complete && len(emergency.Colonists) == 0 && colony.ColonistCount != nil && colony.GetColonistCount() == 0 {
		p.Facts.MoodPawns = domain.Known([]policy.MoodPawn{})
	}
	p.Facts.RecoveryWorkers = recoveryWorkers(p.Facts.MoodPawns)
	p.Facts.Gear = routineGear(p.Facts.Gear, emergency)
	p.Facts.Research = frameResearch(frame.Research)
	p.BuildTier = policy.SelectBuildTier(FinishedResearch(p.Facts.Research), p.PlayerTechLevel)
	p.Facts.Traders = frameTraders(frame.Traders)
	p.Facts.QuestOffers = frameQuests(frame.Quests)
	p.Facts.Prisoners, p.Facts.Custody = domain.Fact[[]policy.PrisonerFacts]{}, domain.Fact[[]policy.CustodyFacts]{}
	if frame.Population != nil {
		p.Facts.Prisoners, p.Facts.Custody = frame.Population.Prisoners, frame.Population.Custody
	}
	var roomCensus *o.RoomsSnapshot
	if rooms {
		source, ok := source.(TemperatureSource)
		if !ok {
			return RoutineReading{}, ErrContract
		}
		if roomCensus, err = readTemperature(ctx, source, id, expected); err != nil {
			return RoutineReading{}, err
		}
		temperature := domain.Unknown[policy.RoomObservation]()
		if roomCensus != nil {
			temperature = temperatureRooms(roomCensus, colonySleeping(colony))
		}
		p.Rooms = temperature
		p.Facts.SleepingMin, p.Facts.SleepingMax = policy.TemperatureRange(temperature)
		p.Facts.Comfort = hostedComfort(p.Facts.Comfort, temperature)
	}
	// The reading spans the frame read and the definitions and room reads
	// beside it: it is observed once they land, under the same age bound.
	reading.StartedAt, reading.ObservedAt = started, clock.Now()
	if reading.ObservedAt.Sub(started) > maxAge {
		return RoutineReading{}, ErrStale
	}
	if err := ctx.Err(); err != nil {
		return RoutineReading{}, err
	}
	return RoutineReading{ColonyReading: reading, Emergency: emergency, Sections: routineSections(frame, *p, roomCensus), Frame: frame}, nil
}

// routinePawns is the frame's colonist pawn detail, validated against the
// frame's complete emergency roster; nil when the roster is incomplete or
// empty, or the frame carries no detail.
func routinePawns(frame bridge.RoutineFrame, id *c.Identity) (*o.PawnSnapshot, error) {
	facts := frame.Emergency.Facts
	complete, known := facts.ColonistsComplete.Value()
	if !known || !complete || len(facts.Colonists) == 0 || frame.Pawns == nil {
		return nil, nil
	}
	ids := make([]string, 0, len(facts.Colonists))
	for _, pawn := range facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	if err := bridge.ValidateRoutinePawnSnapshot(frame.Pawns, id, ids); err != nil {
		return nil, err
	}
	return frame.Pawns, nil
}

// readProjectDefinitions reads the project definitions absent from the
// frame's default planning catalog (census) and returns this planner's own.
func readProjectDefinitions(ctx context.Context, source ColonySource, id *c.Identity, expected Identity, census []PlanningDefinition, definitions []string) ([]PlanningDefinition, error) {
	if len(definitions) == 0 {
		return nil, nil
	}
	if len(definitions) > 256 {
		return nil, ErrContract
	}
	held := map[string]bool{}
	for _, d := range census {
		held[d.Name] = true
	}
	seen := map[string]bool{}
	missing := []string{}
	for _, name := range definitions {
		if !held[name] && !seen[name] {
			missing = append(missing, name)
			seen[name] = true
		}
	}
	if len(missing) == 0 {
		return nil, nil
	}
	// The request carries every name the scheduler's planners pool, so the
	// planners of one step share one read (#599); only this planner's own
	// names join its projection.
	request := DefinitionPoolFrom(ctx).Request(missing, held)
	reply, _, err := source.ReadColonyFacts(ctx, id, true, request)
	if err != nil {
		return nil, err
	}
	extra, err := DecodeColony(reply, expected)
	if err != nil {
		return nil, err
	}
	extra.Identity.Paused = expected.Paused
	if !sameColonyContext(extra.Identity, expected) {
		return nil, ErrChanged
	}
	requested := map[string]bool{}
	for _, name := range request {
		requested[name] = true
	}
	var out []PlanningDefinition
	for _, d := range extra.Definitions {
		if !requested[d.Name] {
			return nil, ErrContract
		}
		if seen[d.Name] {
			out = append(out, d)
		}
	}
	return out, nil
}

// frameQuests is the visible quest census; unknown when the frame carries
// none.
func frameQuests(read *bridge.WorldProgressionRead) domain.Fact[[]policy.JoinerOffer] {
	if read == nil {
		return domain.Unknown[[]policy.JoinerOffer]()
	}
	offers := make([]policy.JoinerOffer, 0, len(read.Quests))
	for _, quest := range read.Quests {
		offers = append(offers, policy.JoinerOffer{Quest: domain.QuestID(quest.ID), ScriptDef: quest.ScriptDef, State: quest.State, CanAccept: quest.CanAccept, RequiresAccepter: quest.RequiresAccepter, ChoiceCount: quest.ChoiceCount})
	}
	return domain.Known(offers)
}

// frameResearch is the research census; unknown when the frame carries
// none.
func frameResearch(read *bridge.ResearchRead) domain.Fact[policy.ResearchFacts] {
	if read == nil {
		return domain.Unknown[policy.ResearchFacts]()
	}
	facts := policy.ResearchFacts{Current: policy.ResearchProjectID(read.CurrentProject)}
	if read.CurrentProject != "" {
		facts.CurrentBenchMissing = policy.ResearchBenchNeeded(read.Projects[read.CurrentProject])
	}
	for name := range read.Projects {
		facts.Projects = append(facts.Projects, policy.ResearchProjectID(name))
	}
	sort.Slice(facts.Projects, func(i, j int) bool { return facts.Projects[i] < facts.Projects[j] })
	for _, name := range read.Finished {
		facts.Finished = append(facts.Finished, policy.ResearchProjectID(name))
	}
	return domain.Known(facts)
}

// frameTraders is the trader census; unknown when the frame carries none.
func frameTraders(read *bridge.TradersRead) domain.Fact[[]policy.TraderFacts] {
	if read == nil {
		return domain.Unknown[[]policy.TraderFacts]()
	}
	rows := make([]policy.TraderFacts, 0, len(read.Traders))
	for _, row := range read.Traders {
		rows = append(rows, policy.TraderFacts{ID: row.ID, Kind: row.Kind, Faction: row.Faction, CanTrade: row.CanTrade, Travelling: row.Travelling, GoodsStacks: int64(row.GoodsStacks)})
	}
	return domain.Known(rows)
}

func hostedComfort(comfort domain.Fact[policy.ComfortObservation], rooms domain.Fact[policy.RoomObservation]) domain.Fact[policy.ComfortObservation] {
	v, known := comfort.Value()
	census, censusKnown := rooms.Value()
	if !known || !censusKnown {
		return domain.Unknown[policy.ComfortObservation]()
	}
	hosted, err := policy.HostedComfort(v, census)
	if err != nil {
		return domain.Unknown[policy.ComfortObservation]()
	}
	return domain.Known(hosted)
}

// FinishedResearch is the finished project list of a known research census,
// the input BuildTier is selected from.
func FinishedResearch(research domain.Fact[policy.ResearchFacts]) domain.Fact[[]policy.ResearchProjectID] {
	if facts, known := research.Value(); known {
		return domain.Known(facts.Finished)
	}
	return domain.Unknown[[]policy.ResearchProjectID]()
}
