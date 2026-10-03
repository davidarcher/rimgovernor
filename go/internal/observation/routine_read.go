package observation

import (
	"context"
	"slices"
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
// (bridge.Client.ReadRoutineFrame) with the load's definition catalog, and
// the load's animal race catalog (bridge.Client.AnimalRaceCatalog, cached per
// load token): an unreadable catalog fails the reading.
type RoutineSource interface {
	ColonySource
	ReadRoutineFrame(context.Context, *c.Identity) (bridge.RoutineFrame, error)
	AnimalRaceCatalog(ctx context.Context, identity *c.Identity) (*bridge.AnimalRaces, error)
}

// RoutineRoyaltySource is the optional royalty read of a RoutineSource
// (bridge.Client.RoyaltyFacts, cached between slow refreshes); a nil result
// means Royalty is not applicable.
type RoutineRoyaltySource interface {
	RoyaltyFacts(ctx context.Context, identity *c.Identity, now int64) (*policy.RoyaltyFacts, error)
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

// ObserveRoutineRooms additionally takes the frame's room census. Temperature
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

func (f frameColony) ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error) {
	return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: f.frame.Colony}}, bridge.Result{}, nil
}

func (f frameColony) FrameTables(context.Context, *c.Identity) (bridge.Tables, error) {
	return f.frame.Tables, nil
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
	reading, err := ObserveColony(ctx, frameColony{source, frame}, clock, expected, maxAge, true)
	if err != nil {
		return RoutineReading{}, err
	}
	p := &reading.Projection
	colony, emergency := frame.Colony, frame.Emergency.Facts
	if frame.Catalog != nil {
		// Room-role furniture rides every routine reading: the catalog
		// names which definitions the planners furnish rooms from.
		roleRows, err := frame.Catalog.RoomRoleRows()
		if err != nil {
			return RoutineReading{}, err
		}
		if p.Definitions, err = frameDefinitionFacts(frame).appendDefinitions(p.Definitions, append(slices.Clone(definitions), roleRows...)); err != nil {
			return RoutineReading{}, err
		}
	}
	if frame.Catalog != nil {
		if p.Facts.Items, err = frame.Catalog.ItemFacts(); err != nil {
			return RoutineReading{}, err
		}
		p.Facts.MedicalReserve.Catalog = p.Facts.Items
	}
	p.Containment = containmentPlanning(frame, p.Definitions)
	if sleeping, known := p.Facts.Sleeping.Value(); known {
		sleeping.BedBuildable = p.DefinitionAvailable(policy.SleepingBedDefinitions[0])
		p.Facts.Sleeping = domain.Known(sleeping)
	}
	p.Facts.CurrentConstruction = domain.Unknown[policy.CurrentConstruction]()
	p.Facts.ConstructionDeficit = domain.Unknown[map[policy.Resource]int64]()
	if census := frame.Buildings; census != nil {
		if census.Invalid != nil {
			return RoutineReading{}, census.Invalid
		}
		if p.Facts.CurrentConstruction, p.Facts.ConstructionDeficit, err = ConstructionFromCensus(census); err != nil {
			return RoutineReading{}, err
		}
	}
	p.Facts.BillReservations = BillReservations(frame.Bills)
	pawns, err := routinePawns(frame, id)
	if err != nil {
		return RoutineReading{}, err
	}
	p.Facts.Armed, p.Facts.Unarmed, p.Facts.DefenseCapacity, p.WorkPawns, p.Facts.MedicalPawns, p.Facts.MoodPawns = domain.Fact[int64]{}, domain.Fact[int64]{}, domain.Fact[float64]{}, domain.Fact[[]policy.WorkPawn]{}, domain.Fact[[]policy.CarePawn]{}, domain.Fact[[]policy.MoodPawn]{}
	if pawns != nil {
		p.Facts.Armed, p.Facts.Unarmed = routineArmed(colony, emergency, pawns, frame.Catalog.CreepJoinerDownsides())
		p.Facts.DefenseCapacity = policy.DefenseCapacity(routineDefenders(colony, emergency, pawns), p.DefenseTurrets)
		var biotech *bridge.BiotechCatalog
		if frame.Catalog != nil {
			biotech = frame.Catalog.Biotech
		}
		p.MechCatalog = biotech.MechCatalog()
		if p.WorkPawns, err = routineWork(colony, emergency, pawns, biotech); err != nil {
			return RoutineReading{}, err
		}
		p.MeditateAvailable = optional(pawns.MeditateAssignmentAvailable)
		p.Facts.MedicalPawns = routineMedical(colony, emergency, pawns)
		p.Facts.MoodPawns = routineMood(colony, emergency, pawns)
	} else if complete, known := emergency.ColonistsComplete.Value(); known && complete && len(emergency.Colonists) == 0 && colony.ColonistCount != nil && colony.GetColonistCount() == 0 {
		p.Facts.MoodPawns = domain.Known([]policy.MoodPawn{})
	}
	p.Mechs = domain.Unknown[policy.MechFleet]()
	if frame.Tables.Pawns.Len() > 0 {
		p.Mechs = domain.Known(MechFleet(frame.Tables.Pawns.Values()))
	}
	p.Facts.MechChargerOwed = policy.MechChargerNeed(p.Mechs, mechChargerFact(p.Biotech))
	p.Facts.RecoveryWorkers = recoveryWorkers(p.Facts.MoodPawns)
	p.Facts.Gear = routineGear(p.Facts.Gear, emergency)
	p.Facts.Research = frameResearch(frame.Research)
	p.BuildTier = policy.SelectBuildTier(FinishedResearch(p.Facts.Research), p.PlayerTechLevel)
	p.Facts.Traders = frameTraders(frame.Traders)
	p.Facts.QuestOffers = frameQuests(frame.Quests, expected.Map)
	p.Facts.Ideology = frameIdeology(frame.Ideology)
	p.Facts.RitualSites = ritualSites(frame.Buildings, p.Facts.Ideology)
	p.Facts.AnimalUpkeep.Animals = policy.ApplyHerdPrecepts(p.Facts.AnimalUpkeep.Animals, p.Facts.Ideology, frame.Catalog != nil && frame.Catalog.Ideology != nil)
	// A failed or inapplicable royalty read leaves the fact unknown; it must
	// not fail the routine reading the whole review stands on.
	p.Facts.Royalty = domain.Unknown[policy.RoyaltyFacts]()
	if royalty, ok := source.(RoutineRoyaltySource); ok {
		if facts, err := royalty.RoyaltyFacts(ctx, id, frame.Colony.GetContext().GetTick()); err == nil && facts != nil {
			p.Facts.Royalty = domain.Known(*facts)
		}
	}
	// The race catalog is static for a load and cached by the source; the
	// herd plan stands on it, so an unreadable catalog fails the reading.
	races, err := source.AnimalRaceCatalog(ctx, id)
	if err != nil {
		return RoutineReading{}, err
	}
	p.Facts.AnimalUpkeep.AnimalRaces = races.AnimalRaceCatalog
	p.Facts.Prisoners, p.Facts.Custody, p.Facts.PrisonerColony, p.Facts.Outlook = domain.Fact[[]policy.PrisonerFacts]{}, domain.Fact[[]policy.CustodyFacts]{}, domain.Fact[policy.PrisonerColony]{}, policy.PopulationOutlook{}
	p.Facts.OwnedNames = domain.Fact[[]policy.OwnedName]{}
	p.Facts.Guests = domain.Fact[[]policy.CarePatient]{}
	if frame.Population != nil {
		p.Facts.Prisoners, p.Facts.Custody, p.Facts.PrisonerColony, p.Facts.Outlook = frame.Population.Prisoners, frame.Population.Custody, frame.Population.Colony, frame.Population.Outlook
		p.Facts.OwnedNames = frame.Population.Names
		p.Facts.Guests = frame.Population.Guests
		if colony, ok := p.Facts.PrisonerColony.Value(); ok {
			colony.Ideology = p.Facts.Ideology
			p.Facts.PrisonerColony = domain.Known(colony)
		}
	}
	// The frame's rooms census is the one room table: room quality reads it
	// on every reading, the room census sections only when asked.
	if frame.Rooms != nil {
		if err := bridge.ValidateTemperatureRooms(frame.Rooms, frame.RoomCells, id); err != nil {
			return RoutineReading{}, err
		}
		if sleeping, known := p.Facts.Sleeping.Value(); known {
			sleeping.Rooms = upkeepRooms(frame.Rooms, sleeping.Beds)
			p.Facts.Sleeping = domain.Known(sleeping)
		}
	}
	var roomCensus *o.RoomsSnapshot
	if rooms {
		roomCensus = frame.Rooms
		temperature := domain.Unknown[policy.RoomObservation]()
		if roomCensus != nil {
			temperature = temperatureRooms(roomCensus, frame.RoomCells, p.Facts.Sleeping)
		}
		p.Rooms = temperature
		p.Facts.SleepingMin, p.Facts.SleepingMax = policy.TemperatureRange(temperature)
		p.Facts.Comfort = hostedComfort(p.Facts.Comfort, temperature)
	}
	// The reading is observed once the frame lands, under the age bound.
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

// frameDefinitionFacts resolves definitions against the frame's catalog,
// research and crop rows.
func frameDefinitionFacts(frame bridge.RoutineFrame) definitionFacts {
	facts := definitionFacts{catalog: frame.Catalog, crops: cropRows(frame.Colony.GetPlanning().GetObserved())}
	if frame.Research != nil {
		facts.finished = finishedSet(frame.Research.Finished)
	}
	return facts
}

// DefinitionCatalog is the frame's catalog, nil for a frame without one.
func (f frameColony) DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error) {
	return f.frame.Catalog, nil
}

// ReadResearch is the frame's research section.
func (f frameColony) ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error) {
	if f.frame.Research == nil {
		return bridge.ResearchRead{}, bridge.Result{}, bridge.ErrUnavailable
	}
	return *f.frame.Research, bridge.Result{}, nil
}

// frameQuests is the visible quest census; unknown when the frame carries
// none.
func frameQuests(read *bridge.WorldProgressionRead, home domain.MapID) domain.Fact[[]policy.JoinerOffer] {
	if read == nil {
		return domain.Unknown[[]policy.JoinerOffer]()
	}
	hostile := map[string]bool{}
	for _, faction := range read.Factions {
		hostile[faction.ID] = faction.Hostile
	}
	offers := make([]policy.JoinerOffer, 0, len(read.Quests))
	for _, quest := range read.Quests {
		offer := policy.JoinerOffer{Quest: domain.QuestID(quest.ID), ScriptDef: quest.ScriptDef, State: quest.State, CanAccept: quest.CanAccept, RequiresAccepter: quest.RequiresAccepter, ChoiceCount: quest.ChoiceCount,
			FactionID: quest.FactionID, FactionHostile: domain.Unknown[bool](), OnMap: quest.MapKnown && domain.MapID(quest.MapID) == home}
		if value, found := hostile[quest.FactionID]; found && quest.FactionID != "" {
			offer.FactionHostile = domain.Known(value)
		}
		for _, favor := range quest.Favor {
			offer.Favor = append(offer.Favor, policy.QuestFavor{Choice: favor.Choice, Favor: favor.Favor})
		}
		offers = append(offers, offer)
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

// frameIdeology is the primary ideoligion; unknown when the frame carries no
// ideology section.
func frameIdeology(read *policy.Ideoligion) domain.Fact[policy.Ideoligion] {
	if read == nil {
		return domain.Unknown[policy.Ideoligion]()
	}
	return domain.Known(*read)
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

// AddDefinitions appends to p the planning rows of names the frame's
// catalog describes and p does not hold yet (a definition only a later
// read names, such as a title's throne).
func (p *ColonyProjection) AddDefinitions(frame bridge.RoutineFrame, names []string) error {
	if frame.Catalog == nil {
		return nil
	}
	var err error
	p.Definitions, err = frameDefinitionFacts(frame).appendDefinitions(p.Definitions, names)
	return err
}
