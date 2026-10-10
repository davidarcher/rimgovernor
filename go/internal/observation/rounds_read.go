package observation

import (
	"context"
	"slices"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// RoundsSource is the routine census: one decoded frame per reading
// (bridge.Client.ReadRoundsFrame) with the load's definition catalog, whose
// race rows are the herd plan's race catalog (DefinitionCatalog.AnimalRaces).
type RoundsSource interface {
	ColonySource
	ReadRoundsFrame(context.Context, *c.Identity) (bridge.RoundsFrame, error)
}

type RoundsReading struct {
	ColonyReading
	Emergency policy.EmergencyFacts
	// Sections is the reading's decoded census, per section with the tick
	// each reply described, for the step's facts.Store.
	Sections RoundsSections
	// Frame is the frame the reading decoded.
	Frame bridge.RoundsFrame
}

func ObserveRoundsOwned(ctx context.Context, source RoundsSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (RoundsReading, error) {
	return observeRounds(ctx, source, clock, expected, maxAge, claims, false, true, definitions...)
}

// ObserveRoundsRooms additionally takes the frame's room census. Temperature
// planning takes room heat from it and comfort takes each facility's
// hosting room role: without the census no comfort facility can be
// certified as hosted, so comfort becomes unknown.
func ObserveRoundsRooms(ctx context.Context, source RoundsSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (RoundsReading, error) {
	return observeRounds(ctx, source, clock, expected, maxAge, claims, true, true, definitions...)
}

// ObserveRoundsProtection decodes the same native frame and room observations
// without fetching the development planning window or its definition reads.
func ObserveRoundsProtection(ctx context.Context, source RoundsSource, clock Clock, expected Identity, maxAge time.Duration) (RoundsReading, error) {
	return observeRounds(ctx, source, clock, expected, maxAge, domain.Unknown[[]policy.ConstructionClaim](), true, false)
}

// frameColony answers ObserveColony's colony facts and zone reads from the
// frame.
type frameColony struct {
	RoundsSource
	frame bridge.RoundsFrame
}

func (f frameColony) ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error) {
	return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: f.frame.Colony}}, bridge.Result{}, nil
}

func (f frameColony) FrameTables(context.Context, *c.Identity) (bridge.Tables, error) {
	tables := f.frame.Tables
	// A power row's wattage includes the upgrades the frame's research
	// census says are finished.
	if f.frame.Research != nil {
		tables.FinishedResearch = domain.Known(slices.Clone(f.frame.Research.Finished))
	}
	return tables, nil
}

func (f frameColony) ReadZoneSection(context.Context, *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	if f.frame.Zones == nil {
		return bridge.ZonesRead{}, bridge.Result{}, bridge.ErrUnavailable
	}
	return *f.frame.Zones, bridge.Result{}, nil
}

func (f frameColony) Zones(ctx context.Context, id *c.Identity) (facts.Held[bridge.ZonesRead], error) {
	read, _, err := f.ReadZoneSection(ctx, id)
	return facts.Held[bridge.ZonesRead]{Value: read, AsOf: read.AsOf, Complete: err == nil, Source: "native_frame"}, err
}

// observeRounds decodes one frame. ObserveColony checks the frame's colony
// context against expected; every other section of the frame shares that
// context and tick, so none is checked against another.
func observeRounds(ctx context.Context, source RoundsSource, clock Clock, expected Identity, maxAge time.Duration, claims domain.Fact[[]policy.ConstructionClaim], rooms, planning bool, definitions ...string) (RoundsReading, error) {
	if source == nil {
		return RoundsReading{}, ErrContract
	}
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	started := clock.Now()
	frame, err := source.ReadRoundsFrame(ctx, id)
	if err != nil {
		return RoundsReading{}, err
	}
	if frame.Colony == nil || frame.Emergency.Context == nil {
		return RoundsReading{}, ErrContract
	}
	colonySource := frameColony{source, frame}
	if !planning {
		// Protection uses only this coherent frame, never the ordinary
		// scheduler's zone refresher and its potentially blocked census.
		ctx = WithZones(ctx, colonySource)
	}
	reading, err := ObserveColony(ctx, colonySource, clock, expected, maxAge, planning)
	if err != nil {
		return RoundsReading{}, err
	}
	p := &reading.Projection
	colony, emergency := frame.Colony, frame.Emergency.Facts
	if frame.Catalog != nil {
		// Room-role furniture rides every routine reading: the catalog
		// names which definitions the planners furnish rooms from.
		roleRows, err := frame.Catalog.RoomRoleRows()
		if err != nil {
			return RoundsReading{}, err
		}
		if p.Shapes, err = frame.Catalog.PieceShapes(); err != nil {
			return RoundsReading{}, err
		}
		names := slices.Concat(definitions, roleRows, p.Shapes.Furniture.Definitions())
		if p.Definitions, err = frameDefinitionFacts(frame).appendDefinitions(p.Definitions, names); err != nil {
			return RoundsReading{}, err
		}
	}
	if frame.Catalog != nil {
		if p.Facts.Items, err = frame.Catalog.ItemFacts(); err != nil {
			return RoundsReading{}, err
		}
		p.Facts.MedicalReserve.Catalog = p.Facts.Items
		if p.Facts.Recipes, err = frame.Catalog.RecipeFacts(); err != nil {
			return RoundsReading{}, err
		}
	}
	p.Facts.Containment = containmentPlanning(frame, p.Definitions)
	if sleeping, known := p.Facts.Sleeping.Value(); known {
		sleeping.BedBuildable = p.DefinitionAvailable(p.Shapes.Furniture.PrimaryBed())
		p.Facts.Sleeping = domain.Known(sleeping)
	}
	p.Facts.CurrentConstruction = domain.Unknown[policy.CurrentConstruction]()
	p.Facts.ConstructionDeficit = domain.Unknown[map[policy.Resource]int64]()
	if census := frame.Buildings; census != nil {
		if census.Invalid != nil {
			return RoundsReading{}, census.Invalid
		}
		if p.Facts.CurrentConstruction, p.Facts.ConstructionDeficit, err = ConstructionFromCensus(census); err != nil {
			return RoundsReading{}, err
		}
	}
	p.Facts.BillReservations = BillReservations(frame.Bills)
	if topology, known := p.PowerPlanning.Value(); known {
		p.Facts.Forward.Power = topology.Networks
	}
	p.Facts.Forward.Turrets = p.DefenseTurrets
	pawns, err := roundsPawns(frame, id)
	if err != nil {
		return RoundsReading{}, err
	}
	p.Facts.Armed, p.Facts.Unarmed, p.Facts.DefenseCapacity, p.WorkPawns, p.Facts.MedicalPawns, p.Facts.MoodPawns = domain.Fact[int64]{}, domain.Fact[int64]{}, domain.Fact[float64]{}, domain.Fact[[]policy.WorkPawn]{}, domain.Fact[[]policy.CarePawn]{}, domain.Fact[[]policy.MoodPawn]{}
	if pawns != nil {
		p.Facts.Armed, p.Facts.Unarmed = roundsArmed(colony, emergency, pawns, frame.Catalog.CreepJoinerDownsides())
		p.Facts.DefenseCapacity = policy.DefenseCapacity(roundsDefenders(colony, emergency, pawns), p.DefenseTurrets)
		var biotech *bridge.BiotechCatalog
		if frame.Catalog != nil {
			biotech = frame.Catalog.Biotech
		}
		p.MechCatalog = biotech.MechCatalog()
		if p.WorkPawns, err = roundsWork(colony, emergency, pawns, frame.Catalog, frame.Tables.Things); err != nil {
			return RoundsReading{}, err
		}
		p.MeditateAvailable = optional(pawns.MeditateAssignmentAvailable)
		if p.Facts.MedicalPawns, err = roundsMedical(colony, emergency, pawns, frame.Catalog); err != nil {
			return RoundsReading{}, err
		}
		p.Facts.MoodPawns = roundsMood(colony, emergency, pawns)
		var thoughtFacts map[string]policy.ThoughtFacts
		if frame.Catalog != nil {
			thoughtFacts = frame.Catalog.AllThoughtFacts()
		}
		p.Facts.MoodLedger = roundsMoodLedger(p.Facts.MoodPawns, pawns, thoughtFacts)
	} else if complete, known := emergency.ColonistsComplete.Value(); known && complete && len(emergency.Colonists) == 0 && colony.ColonistCount != nil && colony.GetColonistCount() == 0 {
		p.Facts.MoodPawns = domain.Known([]policy.MoodPawn{})
		p.Facts.MoodLedger = domain.Known(policy.BuildMoodLedger(nil, nil))
	}
	p.Mechs = domain.Unknown[policy.MechFleet]()
	if frame.Tables.Pawns.Len() > 0 {
		p.Mechs = domain.Known(MechFleet(frame.Tables.Pawns.Values()))
	}
	p.Facts.MechChargerOwed = policy.MechChargerNeed(p.Mechs, mechChargerFact(p.Biotech))
	p.Facts.GeneBankOwed = policy.GeneBankNeed(geneBankFact(p.Biotech))
	p.Facts.RecoveryWorkers = recoveryWorkers(p.Facts.MoodPawns)
	p.Facts.Gear = roundsGear(p.Facts.Gear, emergency)
	p.Facts.Research = frameResearch(frame.Research)
	p.TechTier = policy.SelectTechTier(FinishedResearch(p.Facts.Research), p.PlayerTechLevel)
	p.Facts.Traders = frameTraders(frame.Traders)
	p.Facts.QuestOffers = frameQuests(frame.Quests, expected.Map, frame.Catalog)
	p.Facts.QuestSites = frameWorldSites(frame.Quests)
	p.Facts.QuestExpeditionTrips = frameExpeditionTrips(frame.Quests, expected.Map)
	p.Facts.QuestWorkers = p.WorkPawns
	p.Facts.QuestWorkCapacity, p.Facts.QuestDeparturePawns = frameQuestWorkerCapacity(frame.Quests, expected.Map)
	if pawns != nil {
		p.Facts.QuestDeparturePawns = projectDepartureStrength(p.Facts.QuestDeparturePawns, roundsDefenders(colony, emergency, pawns))
	}
	if frame.Quests != nil && frame.Quests.Context != nil && frame.Quests.Context.Tick != nil {
		p.Facts.QuestObservedTick = domain.Known(domain.Tick(frame.Quests.Context.GetTick()))
	}
	p.Facts.QuestColonistsAtHome = frameQuestColonistsAtHome(frame.Quests, expected.Map, emergency)
	p.Facts.Ideology = frameIdeology(frame.Ideology)
	p.Facts.IdeologyInstalled = frame.IdeologyActive
	p.Facts.RitualSites = ritualSites(frame.Buildings, p.Facts.Ideology)
	ideologyDefs, err := frame.Catalog.IdeologyDefs()
	if err != nil {
		return RoundsReading{}, err
	}
	p.Facts.AnimalUpkeep.Animals = policy.ApplyHerdPrecepts(p.Facts.AnimalUpkeep.Animals, p.Facts.Ideology, ideologyDefs != nil)
	// A pawn row or def the royalty read cannot use leaves the fact unknown; it
	// must not fail the routine reading the whole review stands on.
	p.Facts.Royalty, _ = p.RoyaltyOf(pawns, frame.Catalog)
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
			return RoundsReading{}, err
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
			if planned, ok := temperature.Value(); ok && frame.Catalog != nil {
				if planned.Dining, err = frame.Catalog.DiningFurniture(); err != nil {
					return RoundsReading{}, err
				}
				planned.Shapes = p.Shapes
				temperature = domain.Known(planned)
			}
		}
		p.Rooms = temperature
		p.Facts.SleepingMin, p.Facts.SleepingMax = policy.TemperatureRange(temperature)
		p.Facts.Comfort = hostedComfort(p.Facts.Comfort, temperature)
	}
	// The reading is observed once the frame lands, under the age bound.
	reading.StartedAt, reading.ObservedAt = started, clock.Now()
	if reading.ObservedAt.Sub(started) > maxAge {
		return RoundsReading{}, ErrStale
	}
	if err := ctx.Err(); err != nil {
		return RoundsReading{}, err
	}
	personalShares(p, frame, pawns)
	stampGearShares(p)
	stampGearCreepjoiners(p, pawns, frame.Catalog.CreepJoinerDownsides())
	stampQuestGuestProtection(&p.Facts)
	stampQuestRefugees(&p.Facts)
	return RoundsReading{ColonyReading: reading, Emergency: emergency, Sections: roundsSections(frame, *p, roomCensus), Frame: frame}, nil
}

// roundsPawns is the frame's colonist pawn detail, validated against the
// frame's complete emergency roster; nil when the roster is incomplete or
// empty, or the frame carries no detail.
func roundsPawns(frame bridge.RoundsFrame, id *c.Identity) (*o.PawnSnapshot, error) {
	facts := frame.Emergency.Facts
	complete, known := facts.ColonistsComplete.Value()
	if !known || !complete || len(facts.Colonists) == 0 || frame.Pawns == nil {
		return nil, nil
	}
	ids := make([]string, 0, len(facts.Colonists))
	for _, pawn := range facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	if err := bridge.ValidateRoundsPawnSnapshot(frame.Pawns, id, ids); err != nil {
		return nil, err
	}
	return frame.Pawns, nil
}

// frameDefinitionFacts resolves definitions against the frame's catalog,
// research and crop rows.
func frameDefinitionFacts(frame bridge.RoundsFrame) definitionFacts {
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
// none. Each offer carries the catalog's ground or ship-only class of its
// quest root (bridge.QuestClass), unknown without a catalog or when the
// catalog cannot classify the root.
func frameQuests(read *bridge.WorldProgressionRead, home domain.MapID, catalog *bridge.DefinitionCatalog) domain.Fact[[]policy.JoinerOffer] {
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
			FactionID: quest.FactionID, FactionHostile: domain.Unknown[bool](), OnMap: quest.MapKnown && domain.MapID(quest.MapID) == home, Class: domain.Unknown[policy.QuestClass]()}
		if catalog != nil {
			profile, profileErr := catalog.QuestProfile(quest.ScriptDef)
			if profileErr == nil {
				offer.Profile = domain.Known(profile)
			}
			class, err := catalog.QuestClass(quest.ScriptDef)
			if err == nil {
				offer.Class = domain.Known(class)
			} else {
				offer.ClassError = err.Error()
			}
		}
		if value, found := hostile[quest.FactionID]; found && quest.FactionID != "" {
			offer.FactionHostile = domain.Known(value)
		}
		for _, favor := range quest.Favor {
			offer.Favor = append(offer.Favor, policy.QuestFavor{Choice: favor.Choice, Favor: favor.Favor})
		}
		if quest.RewardChoiceParts != nil {
			offer.RewardChoiceParts = domain.Known(*quest.RewardChoiceParts)
		}
		offer.Asker = domain.PawnID(quest.Asker)
		for _, id := range quest.DeparturePawnIDs {
			offer.DeparturePawnIDs = append(offer.DeparturePawnIDs, domain.PawnID(id))
		}
		if quest.AskerFactionPlayer != nil {
			offer.AskerFactionPlayer = domain.Known(*quest.AskerFactionPlayer)
		}
		if quest.ViolentQuestsAllowed != nil {
			offer.ViolentQuestsAllowed = domain.Known(*quest.ViolentQuestsAllowed)
		}
		offer.ThreatPoints = optional(quest.ThreatPoints)
		for _, reward := range quest.Rewards {
			row := policy.QuestReward{Choice: reward.Choice, Goodwill: reward.Goodwill, Psylink: reward.Psylink, PermitPoints: reward.PermitPoints, Permits: append([]string(nil), reward.Permits...), TitleDef: reward.TitleDef, FactionID: reward.FactionID}
			for _, item := range reward.Items {
				row.Items = append(row.Items, policy.Amount{Resource: policy.Resource(item.Def), Count: item.Count})
			}
			offer.Rewards = append(offer.Rewards, row)
		}
		for _, id := range quest.EligiblePawnIDs {
			offer.EligiblePawnIDs = append(offer.EligiblePawnIDs, domain.PawnID(id))
		}
		if quest.ExpiresInTicks != nil {
			offer.ExpiresInTicks = domain.Known(*quest.ExpiresInTicks)
		}
		for _, objective := range quest.Objectives {
			row := policy.QuestObjective{Kind: objective.Kind, Def: objective.Def, Stuff: objective.Stuff, UnmetRequirement: objective.UnmetRequirement}
			row.MinimumMood = optional(objective.MinimumMood)
			row.Monument = questMonument(objective.Monument)
			row.GravEngine = questGravEngine(objective.GravEngine)
			row.SurveyScanner = questSurveyScanner(objective.SurveyScanner)
			questHackGift(objective, &row)
			row.DurationTicks = optional(objective.DurationTicks)
			row.Workload = questWorkload(objective.Workload, objective.Kind, catalog)
			for _, mood := range objective.LodgerMoods {
				row.LodgerMoods = append(row.LodgerMoods, policy.QuestLodgerMood{Pawn: domain.PawnID(mood.PawnID), Mood: optional(mood.Mood)})
			}
			if objective.Count != nil {
				row.Count = domain.Known(*objective.Count)
			}
			if objective.Active != nil {
				row.Active = domain.Known(*objective.Active)
			}
			if objective.Produced != nil {
				row.Produced = domain.Known(*objective.Produced)
			}
			if objective.DeadlineTicks != nil {
				row.DeadlineTicks = domain.Known(*objective.DeadlineTicks)
			}
			for _, id := range objective.PawnIDs {
				row.PawnIDs = append(row.PawnIDs, domain.PawnID(id))
			}
			offer.Objectives = append(offer.Objectives, row)
		}
		for _, shuttle := range quest.Shuttles {
			row := policy.QuestShuttleState{ID: shuttle.ID, AutoloadAvailable: optional(shuttle.AutoloadAvailable), Autoload: optional(shuttle.Autoload), Loading: optional(shuttle.Loading), AllRequiredLoaded: optional(shuttle.AllRequiredLoaded), ManualLaunchAvailable: optional(shuttle.ManualLaunchAvailable), RequiredColonistCount: optional(shuttle.RequiredColonistCount)}
			for _, id := range shuttle.PawnIDs {
				row.PawnIDs = append(row.PawnIDs, domain.PawnID(id))
			}
			for _, id := range shuttle.LoadedPawnIDs {
				row.LoadedPawnIDs = append(row.LoadedPawnIDs, domain.PawnID(id))
			}
			for _, id := range shuttle.PendingPawnIDs {
				row.PendingPawnIDs = append(row.PendingPawnIDs, domain.PawnID(id))
			}
			offer.Shuttles = append(offer.Shuttles, row)
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
	facts.KnowledgePick = policy.ResearchProjectID(policy.KnowledgePick(read.Projects, read.Finished, read.Knowledge))
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
		rows = append(rows, policy.TraderFacts{Participant: row.Participant, ID: row.ID, Kind: row.Kind, Faction: row.Faction, CanTrade: row.CanTrade, Travelling: row.Travelling, GoodsStacks: int64(row.GoodsStacks)})
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
// the input TechTier is selected from.
func FinishedResearch(research domain.Fact[policy.ResearchFacts]) domain.Fact[[]policy.ResearchProjectID] {
	if facts, known := research.Value(); known {
		return domain.Known(facts.Finished)
	}
	return domain.Unknown[[]policy.ResearchProjectID]()
}

// AddDefinitions appends to p the planning rows of names the frame's
// catalog describes and p does not hold yet (a definition only a later
// read names, such as a title's throne).
func (p *ColonyProjection) AddDefinitions(frame bridge.RoundsFrame, names []string) error {
	if frame.Catalog == nil {
		return nil
	}
	var err error
	p.Definitions, err = frameDefinitionFacts(frame).appendDefinitions(p.Definitions, names)
	return err
}
