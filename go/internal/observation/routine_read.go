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
)

type RoutineSource interface {
	ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadRoutinePawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
	ReadRoutinePopulation(context.Context, *c.Identity) (bridge.PrisonerCensus, bridge.Result, error)
}

// RoutineResearchSource is the optional research read a RoutineSource may
// offer. When present it is read inside the same paused identity bracket as
// the colony census so EnsureResearch's need is measured, not assumed from
// configuration; without it the research fact stays unknown.
type RoutineResearchSource interface {
	ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error)
}

// RoutineQuestSource is the optional visible-quest read a RoutineSource may
// offer, read inside the same bracket for MaintainPopulation's joiner
// census (policy.RoutineFacts.QuestOffers); without it the census stays
// unknown and no offer is answered.
type RoutineQuestSource interface {
	ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error)
}

// RoutineTraderSource is the optional trader census a RoutineSource may
// offer (bridge.ListTraders). Read inside the same paused bracket, it is
// what TradeWithCaravan measures its caravan from; without it the fact
// stays unknown and the goal off.
type RoutineTraderSource interface {
	ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error)
}

type RoutineReading struct {
	ColonyReading
	Emergency          policy.EmergencyFacts
	ResearchReceipt    bridge.Result
	TradersReceipt     bridge.Result
	EmergencyReceipt   bridge.Result
	PawnReceipt        bridge.Result
	DefinitionReceipt  bridge.Result
	TemperatureReceipt bridge.Result
	PopulationReceipt  bridge.Result
	QuestReceipt       bridge.Result
}

type routineBracket struct {
	roomsEnabled       bool
	temperature        domain.Fact[policy.RoomObservation]
	temperatureReceipt bridge.Result
	claims             domain.Fact[[]policy.ConstructionClaim]
	construction       domain.Fact[policy.CurrentConstruction]
	RoutineSource
	expected          Identity
	emergency         bridge.EmergencyObservation
	receipt           bridge.Result
	pawnReceipt       bridge.Result
	population        bridge.PrisonerCensus
	populationReceipt bridge.Result
	research          domain.Fact[policy.ResearchFacts]
	researchReceipt   bridge.Result
	quests            domain.Fact[[]policy.JoinerOffer]
	questReceipt      bridge.Result
	traders           domain.Fact[[]policy.TraderFacts]
	tradersReceipt    bridge.Result
	armed             domain.Fact[int64]
	work              domain.Fact[[]policy.WorkPawn]
	medical           domain.Fact[[]policy.CarePawn]
	mood              domain.Fact[[]policy.MoodPawn]
	definitions       []string
	extraDefinitions  []PlanningDefinition
	definitionReceipt bridge.Result
}

// ReadColonyFacts fans the routine census out inside ObserveColony's
// identity bracket. Every read here is independent of the others (each
// reply carries its own ObservationContext and is validated against the
// expected boundary), so they are issued at once and the bracket costs
// max(read) rather than sum(read) (#227). Two reads chain behind their
// input: the supplementary project definitions need the colony reply's
// default catalog, and the pawn census needs the emergency census's
// colonist ids. Post-processing that combines replies (room temperature
// with colony beds, the work/medical/mood pawns with the colony and
// emergency census) runs after the wave. The first failure wins and cancels
// the rest; the wave's reads are all pure, so nothing needs undoing.
func (s *routineBracket) ReadColonyFacts(ctx context.Context, id *c.Identity, planning bool, defs []string) (*o.ColonyFactsReply, bridge.Result, error) {
	var (
		colony  *o.ColonyFactsReply
		receipt bridge.Result
		pawns   *o.ListPawnsReply
		rooms   *o.RoomsSnapshot
	)
	wave := newReadWave(ctx)
	wave.Go(func(ctx context.Context) error {
		var err error
		colony, receipt, err = s.RoutineSource.ReadColonyFacts(ctx, id, planning, defs)
		if err != nil {
			return err
		}
		return s.readProjectDefinitions(ctx, id, colony)
	})
	wave.Go(func(ctx context.Context) error { return s.readConstruction(ctx, id) })
	wave.Go(func(ctx context.Context) error {
		var err error
		pawns, err = s.readEmergency(ctx, id)
		return err
	})
	wave.Go(func(ctx context.Context) error { return s.readPopulation(ctx, id) })
	wave.Go(func(ctx context.Context) error { return s.readResearch(ctx, id) })
	wave.Go(func(ctx context.Context) error { return s.readQuests(ctx, id) })
	wave.Go(func(ctx context.Context) error { return s.readTraders(ctx, id) })
	wave.Go(func(ctx context.Context) error {
		var err error
		rooms, err = s.readTemperature(ctx, id)
		return err
	})
	if err := wave.Wait(); err != nil {
		return nil, receipt, err
	}
	if rooms != nil {
		s.temperature = temperatureRooms(rooms, colonySleeping(colony.GetObserved()))
	}
	if pawns != nil {
		s.armed = routineArmed(colony.GetObserved(), s.emergency.Facts, pawns.GetObserved())
		s.work = routineWork(colony.GetObserved(), s.emergency.Facts, pawns.GetObserved())
		s.medical = routineMedical(colony.GetObserved(), s.emergency.Facts, pawns.GetObserved())
		s.mood = routineMood(colony.GetObserved(), s.emergency.Facts, pawns.GetObserved())
	} else if complete, known := s.emergency.Facts.ColonistsComplete.Value(); known && complete && len(s.emergency.Facts.Colonists) == 0 && colony.GetObserved().ColonistCount != nil && colony.GetObserved().GetColonistCount() == 0 {
		s.mood = domain.Known([]policy.MoodPawn{})
	}
	return colony, receipt, nil
}

// readEmergency reads the emergency census and, when it names a complete
// colonist roster, the routine pawn census behind it. The pawn reply is
// returned rather than projected: the projections also need the colony
// reply, which is in flight on another lane of the wave.
func (s *routineBracket) readEmergency(ctx context.Context, id *c.Identity) (*o.ListPawnsReply, error) {
	var err error
	s.emergency, s.receipt, err = s.ReadEmergency(ctx, id)
	if err != nil {
		return nil, err
	}
	identity, err := contextIdentity(s.emergency.Context)
	if err != nil {
		return nil, err
	}
	identity.Paused = s.expected.Paused
	if !cachedColonyBoundary(identity, s.expected, bridge.FactEmergency) {
		return nil, ErrChanged
	}
	complete, known := s.emergency.Facts.ColonistsComplete.Value()
	if !known || !complete {
		return nil, nil
	}
	ids := make([]string, 0, len(s.emergency.Facts.Colonists))
	for _, pawn := range s.emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	if len(ids) == 0 {
		return nil, nil
	}
	pawns, pawnReceipt, err := s.ReadRoutinePawns(ctx, id, ids)
	s.pawnReceipt = pawnReceipt
	if err != nil {
		return nil, err
	}
	if pawns == nil || pawns.GetObserved() == nil {
		return nil, ErrContract
	}
	if err = bridge.ValidateRoutinePawnSnapshot(pawns.GetObserved(), id, ids); err != nil {
		return nil, err
	}
	observed, err := contextIdentity(pawns.GetObserved().Context)
	if err != nil {
		return nil, err
	}
	observed.Paused = s.expected.Paused
	if !cachedColonyBoundary(observed, s.expected, bridge.FactPawns) {
		return nil, ErrChanged
	}
	return pawns, nil
}

func (s *routineBracket) readPopulation(ctx context.Context, id *c.Identity) error {
	var err error
	s.population, s.populationReceipt, err = s.ReadRoutinePopulation(ctx, id)
	if err != nil {
		return err
	}
	identity, err := contextIdentity(s.population.Context)
	if err != nil {
		return err
	}
	identity.Paused = s.expected.Paused
	if !cachedColonyBoundary(identity, s.expected, bridge.FactPawns) {
		return ErrChanged
	}
	return nil
}

func ObserveRoutine(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, definitions ...string) (RoutineReading, error) {
	return ObserveRoutineOwned(ctx, source, clock, expected, maxAge, domain.Unknown[[]policy.ConstructionClaim](), definitions...)
}
func ObserveRoutineOwned(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (RoutineReading, error) {
	return observeRoutine(ctx, source, clock, expected, maxAge, claims, false, definitions...)
}

// ObserveRoutineRooms additionally reads the typed room census inside the
// same paused bracket. Temperature planning takes room heat from it and
// comfort takes each facility's hosting room role: without the census no
// comfort facility can be certified as hosted, so comfort becomes unknown.
func ObserveRoutineRooms(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (RoutineReading, error) {
	return observeRoutine(ctx, source, clock, expected, maxAge, claims, true, definitions...)
}

func observeRoutine(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], rooms bool, definitions ...string) (RoutineReading, error) {
	if source == nil {
		return RoutineReading{}, ErrContract
	}
	bracket := &routineBracket{roomsEnabled: rooms, claims: claims, RoutineSource: source, expected: expected, definitions: append([]string(nil), definitions...)}
	reading, err := ObserveColony(ctx, bracket, clock, expected, maxAge, true, nil)
	if err != nil {
		return RoutineReading{}, err
	}
	reading.Projection.Facts.CurrentConstruction = bracket.construction
	reading.Projection.Facts.Armed = bracket.armed
	reading.Projection.WorkPawns = bracket.work
	reading.Projection.Facts.MedicalPawns = bracket.medical
	reading.Projection.Facts.MoodPawns = bracket.mood
	reading.Projection.Facts.RecoveryWorkers = recoveryWorkers(bracket.mood)
	reading.Projection.Facts.Gear = routineGear(reading.Projection.Facts.Gear, bracket.emergency.Facts)
	reading.Projection.Facts.Research = bracket.research
	reading.Projection.Facts.Traders = bracket.traders
	reading.Projection.Facts.Prisoners = bracket.population.Prisoners
	reading.Projection.Facts.Custody = bracket.population.Custody
	reading.Projection.Facts.QuestOffers = bracket.quests
	reading.Projection.Definitions = append(reading.Projection.Definitions, bracket.extraDefinitions...)
	if rooms {
		reading.Projection.Rooms = bracket.temperature
		reading.Projection.Facts.SleepingMin, reading.Projection.Facts.SleepingMax = policy.TemperatureRange(bracket.temperature)
		reading.Projection.Facts.Comfort = hostedComfort(reading.Projection.Facts.Comfort, bracket.temperature)
	}
	return RoutineReading{ColonyReading: reading, Emergency: bracket.emergency.Facts, EmergencyReceipt: bracket.receipt, PawnReceipt: bracket.pawnReceipt, DefinitionReceipt: bracket.definitionReceipt, TemperatureReceipt: bracket.temperatureReceipt, PopulationReceipt: bracket.populationReceipt, ResearchReceipt: bracket.researchReceipt, QuestReceipt: bracket.questReceipt, TradersReceipt: bracket.tradersReceipt}, nil
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
	if !cachedColonyBoundary(extra.Identity, s.expected, bridge.FactColony) {
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

// readQuests mirrors readResearch for the visible quest census: a source
// without the read leaves the census unknown, and a snapshot from a
// different colony boundary invalidates the whole reading.
func (s *routineBracket) readQuests(ctx context.Context, id *c.Identity) error {
	source, ok := s.RoutineSource.(RoutineQuestSource)
	if !ok {
		return nil
	}
	read, receipt, err := source.ReadWorldProgression(ctx, id, false)
	s.questReceipt = receipt
	if err != nil {
		return err
	}
	identity, err := contextIdentity(read.Context)
	if err != nil {
		return err
	}
	identity.Paused = s.expected.Paused
	if !cachedColonyBoundary(identity, s.expected, bridge.FactColony) {
		return ErrChanged
	}
	offers := make([]policy.JoinerOffer, 0, len(read.Quests))
	for _, quest := range read.Quests {
		offers = append(offers, policy.JoinerOffer{Quest: domain.QuestID(quest.ID), ScriptDef: quest.ScriptDef, State: quest.State, CanAccept: quest.CanAccept, RequiresAccepter: quest.RequiresAccepter, ChoiceCount: quest.ChoiceCount})
	}
	s.quests = domain.Known(offers)
	return nil
}

// readResearch stays inside the colony identity bracket: a source without a
// research read leaves the fact unknown, and a research snapshot from a
// different colony boundary invalidates the whole reading.
func (s *routineBracket) readResearch(ctx context.Context, id *c.Identity) error {
	source, ok := s.RoutineSource.(RoutineResearchSource)
	if !ok {
		return nil
	}
	read, receipt, err := source.ReadResearch(ctx, id)
	s.researchReceipt = receipt
	if err != nil {
		return err
	}
	identity, err := contextIdentity(read.Context)
	if err != nil {
		return err
	}
	identity.Paused = s.expected.Paused
	if !cachedColonyBoundary(identity, s.expected, bridge.FactResearch) {
		return ErrChanged
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
	s.research = domain.Known(facts)
	return nil
}

// readTraders mirrors readResearch: a source without the census leaves the
// fact unknown, and a census from another colony boundary invalidates the
// reading.
func (s *routineBracket) readTraders(ctx context.Context, id *c.Identity) error {
	source, ok := s.RoutineSource.(RoutineTraderSource)
	if !ok {
		return nil
	}
	read, receipt, err := source.ListTraders(ctx, id)
	s.tradersReceipt = receipt
	if err != nil {
		return err
	}
	identity, err := contextIdentity(read.Context)
	if err != nil {
		return err
	}
	identity.Paused = s.expected.Paused
	if !cachedColonyBoundary(identity, s.expected, bridge.FactColony) {
		return ErrChanged
	}
	rows := make([]policy.TraderFacts, 0, len(read.Traders))
	for _, row := range read.Traders {
		rows = append(rows, policy.TraderFacts{ID: row.ID, Kind: row.Kind, Faction: row.Faction, CanTrade: row.CanTrade, Travelling: row.Travelling, GoodsStacks: int64(row.GoodsStacks)})
	}
	s.traders = domain.Known(rows)
	return nil
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
