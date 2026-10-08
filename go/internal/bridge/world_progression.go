package bridge

import (
	"context"
	"math"
	"sort"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// WorldRouteFact is one native player-home route row (WorldRoute proto) from
// a caravan's home_routes census (NativeWorldProgressionObservation.cs's
// HomeRoutes(Caravan) helper populates one row per player-home map).
// EstimatedTicksKnown is false when native reports no travel-time estimate
// for that route (for example, an unreachable route); Reachable is always
// populated. Nothing here proves the route is still valid at dispatch time --
// it is only ever the world-evaluation advisory's read of a same-tick census.
type WorldRouteFact struct {
	DestinationTile     int32
	Reachable           bool
	EstimatedTicks      int64
	EstimatedTicksKnown bool
}

// CaravanPawnFact is one crew member's identity and dead/downed status from
// a caravan's world-progression pawn census. DeadKnown/DownedKnown are false
// only when native's own optional PawnState fields are absent; the
// world-evaluation advisory's health check treats an absent fact the same
// as "not proven alive" -- not healthy.
type CaravanPawnFact struct {
	ID          string
	Dead        bool
	DeadKnown   bool
	Downed      bool
	DownedKnown bool
}

// CaravanJourney is the validated subset of one player caravan's world-progression
// census row that travel/arrival tracking and the read-only world-evaluation
// advisory need: its native identity, whether it is still moving, the crew
// currently aboard (with dead/downed status), its home routes and remaining
// travel food, and its full cargo census. It exists so a boundary can tell
// "still travelling" from "no longer a caravan" (the World Object disappears
// once its pawns enter any map, home or foreign); a caravan departure's
// receipt only reports that formation started. This type does not surface CaravanState.pawns' remaining
// PawnState fields (needs, health details, and so on) or mass fields; a
// future slice adds those once a caller needs them. Inventory is the full
// carried cargo; native always populates the full inventory census
// (Inventory() has no failure path), so an absent row is a known zero, not
// unknown (the currency is ItemFacts.Currency). FoodDaysKnown mirrors that same "absent is a
// known fact, not a gap" discipline for CaravanState.food_days, which native
// reports as an optional double.
type CaravanJourney struct {
	Destination   *int32
	ID            string
	Tile          int32
	Moving        bool
	PawnIDs       []string
	Pawns         []CaravanPawnFact
	FoodDays      float64
	FoodDaysKnown bool
	HomeRoutes    []WorldRouteFact
	// Inventory is the full per-caravan cargo census (defName -> units),
	// aggregated by native across every pawn aboard
	// (CaravanInventoryUtility.AllInventoryItems). The read-only
	// world-evaluation advisory treats this caravan-level total as a
	// caravan's carried cargo rather than re-deriving it pawn by pawn, a
	// narrower but equivalent read of the same native aggregate a per-pawn
	// inventory sum would produce.
	Inventory map[string]int64
}

// QuestOffer is the validated subset of one WorldProgressionSnapshot.quests
// row (NativeWorldProgressionObservation.cs's Quests()) that the quest-accept
// boundary needs: identity, native settled state, whether an accepter pawn is
// required and which of the currently eligible colonists may supply it,
// native's own CanAcceptQuest verdict, the exact count of options in the
// quest's single native reward-choice part (0 when it carries none, so a
// caller never has to guess; the player names an exact index in
// [0, ChoiceCount) via QuestAccept.RewardChoice, whether that count is one
// option or many -- native itself refuses any quest exposing two or more
// separate QuestPart_Choice parts, so ChoiceCount never needs to represent
// that shape), and whether it also carries a settlement trade objective.
// It does not surface reward item contents; nothing here picks a quest or a
// reward, it only proves facts about one already-selected quest and option.
type QuestOffer struct {
	ThreatPoints *float64
	ID           string
	// ScriptDef is the root QuestScriptDef defName (Quest.root), "" for a
	// quest built without one; policy.IsJoinerOffer tells a joiner offer
	// from every other quest by it.
	ScriptDef        string
	State            string
	RequiresAccepter bool
	CanAccept        bool
	ChoiceCount      int32
	// TradeRequests is the quest's full native settlement trade objective
	// list (QuestTradeRequest rows) the world-evaluation advisory reads.
	TradeRequests    []QuestTradeRequestFact
	EligiblePawnIDs  []string
	DeparturePawnIDs []string
	SnapshotToken    string
	// FactionID is the first non-player faction the quest involves ("" when
	// none), the id FactionState rows carry. MapID is the map the quest's
	// look targets sit on; MapKnown is false for a quest anchored only to a
	// world object. Favor is the Empire favor each reward choice grants,
	// ascending by choice, omitting choices that grant none.
	FactionID            string
	MapID                int32
	MapKnown             bool
	Favor                []QuestFavorFact
	Objectives           []QuestObjectiveFact
	Shuttles             []QuestShuttleFact
	ExpiresInTicks       *int64
	RewardChoiceParts    *int32
	Rewards              []QuestRewardFact
	Asker                string
	AskerFactionPlayer   *bool
	ViolentQuestsAllowed *bool
}

type QuestRewardFact struct {
	Choice       int32
	Items        []QuestTradeItemFact
	Goodwill     int32
	Psylink      int32
	PermitPoints int32
	Permits      []string
	TitleDef     string
	FactionID    string
}

type QuestTradeItemFact struct {
	Def   string
	Count int64
}

// QuestObjectiveFact preserves typed native progress; absent fields stay absent.
type QuestObjectiveFact struct {
	Monument         *o.QuestMonument
	GravEngine       *o.QuestGravEngine
	SurveyScanner    *o.QuestSurveyScanner
	Kind             o.QuestObjectiveKind
	Def              string
	Stuff            string
	Count            *int64
	Produced         *int64
	DeadlineTicks    *int64
	DurationTicks    *int64
	Workload         *o.QuestWorkload
	UnmetRequirement string
	PawnIDs          []string
	Active           *bool
	MinimumMood      *float64
	LodgerMoods      []QuestLodgerMoodFact
}
type QuestLodgerMoodFact struct {
	PawnID string
	Mood   *float64
}
type QuestShuttleFact struct {
	ID                                                                             string
	AutoloadAvailable, Autoload, Loading, AllRequiredLoaded, ManualLaunchAvailable *bool
	PawnIDs, LoadedPawnIDs                                                         []string
	PendingPawnIDs                                                                 []string
	RequiredColonistCount                                                          *int32
}

// QuestFavorFact is the royal favor one reward choice grants in total.
type QuestFavorFact struct {
	Choice int32
	Favor  int32
}

// FactionFact is the validated subset of one WorldProgressionSnapshot.factions
// row the quest planners join to a quest's faction_id.
type FactionFact struct {
	ID      string
	Player  bool
	Hostile bool
}

// QuestTradeRequestFact is one native settlement trade objective row
// (QuestTradeRequest proto) from a quest's trade_requests census: the
// required resource def name and count, and the settlement's destination
// tile. Nothing here proves the objective is still live or that any
// particular caravan can fulfill it -- it is only the world-evaluation
// advisory's read of a same-tick census.
type QuestTradeRequestFact struct {
	Resource        string
	Count           int64
	DestinationTile int32
}

// WorldMap is the validated subset of one WorldProgressionSnapshot.maps row
// that failure-recovery classification needs: which map this is (native
// uniqueID) and whether native marks it a player home map, plus the pawn
// IDs native reports currently spawned there. Tile is surfaced because it is
// this codebase's only home-tile source: a caller wanting the colony's home
// world-map tile (e.g. to feed ReadWorld's longitude lookup) finds the row
// with Home true and reads its Tile. It does not surface label or stored
// items; nothing here reads or writes anything on a foreign map, it only
// lets a caller tell "this pawn is alive and visible on some live map" from
// "absent from every census we can read".
type WorldMap struct {
	ID           int32
	Tile         int32
	Home         bool
	PawnIDs      []string
	QuestWorkers []*o.QuestWorker
}

// WorldProgressionRead is the validated subset of one rimgovernor/
// observations_read_world_progression census the world-site and
// quest boundaries need. It does not surface
// WorldProgressionSnapshot.factions or assemblies. Maps is read only for
// its pawn rosters (see WorldMap): a caravan whose world object has
// disappeared but whose crew is visible on some non-home map is known to be
// on a live map (an encounter/ambush map, most likely), not lost -- without
// ever claiming custody of that map's pawns or issuing any write to it.
type WorldProgressionRead struct {
	Assemblies []CaravanAssemblyFact
	Sites      []*o.WorldSite
	Context    *c.ObservationContext
	Maps       []WorldMap
	Caravans   []CaravanJourney
	Quests     []QuestOffer
	Factions   []FactionFact
}

// worldProgressionRequest is the census read, shared with the bundle's
// world progression family (#593).
func worldProgressionRequest(identity *c.Identity, includeStorage bool) *o.WorldProgressionRequest {
	return &o.WorldProgressionRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, IncludeStorage: proto.Bool(includeStorage)}
}

func (client *Client) ReadWorldProgression(ctx context.Context, identity *c.Identity, includeStorage bool) (WorldProgressionRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return WorldProgressionRead{}, Result{}, err
	}
	request := worldProgressionRequest(identity, includeStorage)
	reply := &o.WorldProgressionReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_world_progression", request, reply)
	if err != nil {
		return WorldProgressionRead{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return WorldProgressionRead{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.WorldProgressionReply_Failure:
		return WorldProgressionRead{}, raw, failure(v.Failure, raw)
	case *o.WorldProgressionReply_Unavailable:
		return WorldProgressionRead{}, raw, unavailable(v.Unavailable, raw)
	case *o.WorldProgressionReply_Observed:
		out, err := worldProgressionSelected(v.Observed, identity)
		return out, raw, err
	default:
		return WorldProgressionRead{}, raw, contract("world progression outcome missing")
	}
}

func worldProgressionSelected(v *o.WorldProgressionSnapshot, identity *c.Identity) (WorldProgressionRead, error) {
	if v == nil {
		return WorldProgressionRead{}, contract("world progression snapshot missing")
	}
	if err := ValidateContext(v.Context); err != nil {
		return WorldProgressionRead{}, err
	}
	if !sameIdentity(v.Context.Identity, identity) {
		return WorldProgressionRead{}, contract("world progression world mismatch")
	}
	maps := make([]WorldMap, len(v.Maps))
	seenMapPawns := map[string]bool{}
	for i, row := range v.Maps {
		if row == nil || row.Id == nil || row.Tile == nil || row.GetTile() < 0 || row.Home == nil {
			return WorldProgressionRead{}, contract("invalid world progression map")
		}
		pawnIDs := make([]string, len(row.Pawns))
		seen := map[string]bool{}
		for j, pawn := range row.Pawns {
			if pawn == nil || pawn.Pawn == nil || validID(pawn.Pawn.GetId()) != nil || seen[pawn.Pawn.GetId()] {
				return WorldProgressionRead{}, contract("invalid or duplicate world progression map pawn")
			}
			seen[pawn.Pawn.GetId()] = true
			// A pawn spawned on two maps at once is not a real game state;
			// treat it as evidence the census cannot be trusted rather than
			// picking one map to believe.
			if seenMapPawns[pawn.Pawn.GetId()] {
				return WorldProgressionRead{}, contract("world progression pawn present on multiple maps")
			}
			seenMapPawns[pawn.Pawn.GetId()] = true
			pawnIDs[j] = pawn.Pawn.GetId()
		}
		workers, err := validatedQuestWorkers(row.QuestWorkers)
		if err != nil {
			return WorldProgressionRead{}, err
		}
		maps[i] = WorldMap{ID: row.GetId(), Tile: row.GetTile(), Home: row.GetHome(), PawnIDs: pawnIDs, QuestWorkers: workers}
	}
	seen := map[string]bool{}
	rows := make([]CaravanJourney, len(v.Caravans))
	for i, row := range v.Caravans {
		if row == nil || row.Caravan == nil || validID(row.Caravan.GetId()) != nil || seen[row.Caravan.GetId()] {
			return WorldProgressionRead{}, contract("invalid or duplicate world progression caravan")
		}
		seen[row.Caravan.GetId()] = true
		if row.Tile == nil || row.GetTile() < 0 || row.GetDestination() < 0 {
			return WorldProgressionRead{}, contract("invalid world progression caravan tile or crew")
		}
		pawnIDs := make([]string, len(row.Pawns))
		pawns := make([]CaravanPawnFact, len(row.Pawns))
		seenPawns := map[string]bool{}
		for j, pawn := range row.Pawns {
			if pawn == nil || pawn.Pawn == nil || validID(pawn.Pawn.GetId()) != nil || seenPawns[pawn.Pawn.GetId()] {
				return WorldProgressionRead{}, contract("invalid or duplicate world progression caravan pawn")
			}
			seenPawns[pawn.Pawn.GetId()] = true
			pawnIDs[j] = pawn.Pawn.GetId()
			fact := CaravanPawnFact{ID: pawn.Pawn.GetId()}
			if pawn.Dead != nil {
				fact.Dead, fact.DeadKnown = pawn.GetDead(), true
			}
			if pawn.Downed != nil {
				fact.Downed, fact.DownedKnown = pawn.GetDowned(), true
			}
			pawns[j] = fact
		}
		inventory := make(map[string]int64, len(row.Inventory))
		seenDefs := map[string]bool{}
		for _, item := range row.Inventory {
			if item == nil || item.DefName == nil || seenDefs[item.GetDefName()] || item.Units == nil || item.GetUnits() < 0 {
				return WorldProgressionRead{}, contract("invalid or duplicate world progression caravan inventory row")
			}
			seenDefs[item.GetDefName()] = true
			inventory[item.GetDefName()] = item.GetUnits()
		}
		routes := make([]WorldRouteFact, len(row.HomeRoutes))
		for k, route := range row.HomeRoutes {
			if route == nil || route.Destination == nil || route.GetDestination() < 0 || route.Reachable == nil {
				return WorldProgressionRead{}, contract("invalid world progression caravan home route")
			}
			fact := WorldRouteFact{DestinationTile: route.GetDestination(), Reachable: route.GetReachable()}
			if route.EstimatedTicks != nil {
				if route.GetEstimatedTicks() < 0 {
					return WorldProgressionRead{}, contract("invalid world progression caravan home route ticks")
				}
				fact.EstimatedTicks, fact.EstimatedTicksKnown = route.GetEstimatedTicks(), true
			}
			routes[k] = fact
		}
		if !combatNumber(row.FoodDays, true) {
			return WorldProgressionRead{}, contract("invalid world progression caravan food days")
		}
		journey := CaravanJourney{ID: row.Caravan.GetId(), Tile: row.GetTile(), Moving: row.GetMoving(), PawnIDs: pawnIDs, Pawns: pawns, HomeRoutes: routes, Inventory: inventory}
		journey.Destination = row.Destination
		if row.FoodDays != nil {
			journey.FoodDays, journey.FoodDaysKnown = row.GetFoodDays(), true
		}
		rows[i] = journey
	}
	seenQuests := map[string]bool{}
	quests := make([]QuestOffer, len(v.Quests))
	for i, row := range v.Quests {
		if row == nil || validID(row.GetId()) != nil || seenQuests[row.GetId()] || QuestStatusName(row.GetState()) == "" || row.RequiresAccepter == nil || row.CanAccept == nil {
			return WorldProgressionRead{}, contract("invalid or duplicate world progression quest")
		}
		seenQuests[row.GetId()] = true
		if row.Snapshot == nil || row.Snapshot.GetEntityId() != row.GetId() || validID(row.Snapshot.GetToken()) != nil {
			return WorldProgressionRead{}, contract("world progression quest CAS token unavailable")
		}
		choices := map[uint32]bool{}
		favor := map[int32]int32{}
		for _, reward := range row.Rewards {
			if reward == nil || reward.GetFavor() < 0 || reward.GetPsylink() < 0 || reward.GetPermitPoints() < 0 {
				return WorldProgressionRead{}, contract("invalid world progression quest reward")
			}
			choice := int32(-1)
			if reward.ChoiceIndex != nil {
				if reward.GetChoiceIndex() > 2147483647 {
					return WorldProgressionRead{}, contract("invalid quest choice index")
				}
				choice = int32(reward.GetChoiceIndex())
				choices[reward.GetChoiceIndex()] = true
			}
			if reward.GetFavor() > 0 {
				if int64(favor[choice])+int64(reward.GetFavor()) > 2147483647 {
					return WorldProgressionRead{}, contract("quest favor overflow")
				}
				favor[choice] += reward.GetFavor()
			}
		}
		pawnIDs := make([]string, len(row.EligiblePawns))
		seenPawns := map[string]bool{}
		for j, pawn := range row.EligiblePawns {
			if pawn == nil || validID(pawn.GetId()) != nil || seenPawns[pawn.GetId()] {
				return WorldProgressionRead{}, contract("invalid or duplicate world progression quest accepter")
			}
			seenPawns[pawn.GetId()] = true
			pawnIDs[j] = pawn.GetId()
		}
		quest := QuestOffer{
			ID: row.GetId(), ScriptDef: row.GetScriptDef(), State: QuestStatusName(row.GetState()), RequiresAccepter: row.GetRequiresAccepter(), CanAccept: row.GetCanAccept(),
			ChoiceCount: int32(len(choices)), EligiblePawnIDs: pawnIDs, SnapshotToken: row.Snapshot.GetToken(),
		}
		for _, id := range row.DeparturePawnIds {
			if validID(id) != nil {
				return WorldProgressionRead{}, contract("invalid quest departure pawn")
			}
			quest.DeparturePawnIDs = append(quest.DeparturePawnIDs, id)
		}
		quest.FactionID, quest.MapKnown = row.GetFactionId(), row.MapId != nil
		quest.MapID = row.GetMapId()
		quest.ExpiresInTicks = row.ExpiresInTicks
		if row.AskerPawnId != nil && validID(row.GetAskerPawnId()) != nil {
			return WorldProgressionRead{}, contract("invalid quest asker")
		}
		quest.Asker, quest.AskerFactionPlayer, quest.ViolentQuestsAllowed = row.GetAskerPawnId(), row.AskerFactionPlayer, row.ViolentQuestsAllowed
		if row.ThreatPoints != nil && !math.IsNaN(*row.ThreatPoints) && !math.IsInf(*row.ThreatPoints, 0) && *row.ThreatPoints >= 0 {
			quest.ThreatPoints = row.ThreatPoints
		}
		if row.GetChoicePartCount() < 0 {
			return WorldProgressionRead{}, contract("invalid quest choice parts")
		}
		quest.RewardChoiceParts = row.ChoicePartCount
		for _, reward := range row.Rewards {
			choice := int32(-1)
			if reward.ChoiceIndex != nil {
				choice = int32(reward.GetChoiceIndex())
			}
			fact := QuestRewardFact{Choice: choice, Goodwill: reward.GetGoodwill(), Psylink: reward.GetPsylink(), PermitPoints: reward.GetPermitPoints(), Permits: append([]string(nil), reward.Permits...), TitleDef: reward.GetTitleDef(), FactionID: reward.GetFactionId()}
			for _, item := range reward.Items {
				if item == nil || validID(item.GetDefName()) != nil || item.Units == nil || item.GetUnits() < 0 {
					return WorldProgressionRead{}, contract("invalid quest reward item")
				}
				fact.Items = append(fact.Items, QuestTradeItemFact{Def: item.GetDefName(), Count: item.GetUnits()})
			}
			seenPermits := map[string]bool{}
			for _, permit := range fact.Permits {
				if validID(permit) != nil || seenPermits[permit] {
					return WorldProgressionRead{}, contract("invalid quest reward permit")
				}
				seenPermits[permit] = true
			}
			if fact.TitleDef != "" && validID(fact.TitleDef) != nil || fact.FactionID != "" && validID(fact.FactionID) != nil {
				return WorldProgressionRead{}, contract("invalid quest reward reference")
			}
			quest.Rewards = append(quest.Rewards, fact)
		}
		for _, objective := range row.Objectives {
			if objective == nil || objective.Kind == nil || objective.GetKind() < o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_UNKNOWN || objective.GetKind() > o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HOLD_SURVEY_SCANNER || objective.GetCount() < 0 || objective.GetProduced() < 0 || objective.GetDeadlineTicks() < 0 || objective.GetDurationTicks() < 0 {
				return WorldProgressionRead{}, contract("invalid quest objective")
			}
			seen := map[string]bool{}
			for _, id := range objective.PawnIds {
				if validID(id) != nil || seen[id] {
					return WorldProgressionRead{}, contract("invalid quest objective pawn")
				}
				seen[id] = true
			}
			fact := QuestObjectiveFact{Kind: objective.GetKind(), Def: objective.GetDef(), Stuff: objective.GetStuff(), Count: objective.Count, Produced: objective.Produced, DeadlineTicks: objective.DeadlineTicks, UnmetRequirement: objective.GetUnmetRequirement(), PawnIDs: append([]string(nil), objective.PawnIds...), Active: objective.Active, MinimumMood: objective.MinimumMood}
			fact.Monument = validatedQuestMonument(objective.Monument)
			if engine, err := validatedQuestGravEngine(objective.GravEngine); err != nil {
				return WorldProgressionRead{}, err
			} else {
				fact.GravEngine = engine
			}
			fact.DurationTicks = objective.DurationTicks
			if survey, err := validatedQuestSurvey(objective.SurveyScanner); err != nil {
				return WorldProgressionRead{}, err
			} else {
				fact.SurveyScanner = survey
			}
			if w := objective.Workload; w != nil {
				if w.Work == nil || w.RateFactor == nil || math.IsNaN(w.GetWork()) || math.IsInf(w.GetWork(), 0) || w.GetWork() < 0 || math.IsNaN(w.GetRateFactor()) || math.IsInf(w.GetRateFactor(), 0) || w.GetRateFactor() < 0 {
					return WorldProgressionRead{}, contract("invalid quest workload")
				}
				fact.Workload = proto.Clone(w).(*o.QuestWorkload)
			}
			if objective.MinimumMood != nil && (math.IsNaN(*objective.MinimumMood) || math.IsInf(*objective.MinimumMood, 0) || *objective.MinimumMood < 0 || *objective.MinimumMood > 1) {
				return WorldProgressionRead{}, contract("invalid quest mood threshold")
			}
			seenMoods := map[string]bool{}
			for _, mood := range objective.LodgerMoods {
				if mood == nil || validID(mood.GetPawnId()) != nil || seenMoods[mood.GetPawnId()] || mood.Mood != nil && (math.IsNaN(*mood.Mood) || math.IsInf(*mood.Mood, 0) || *mood.Mood < 0 || *mood.Mood > 1) {
					return WorldProgressionRead{}, contract("invalid quest lodger mood")
				}
				seenMoods[mood.GetPawnId()] = true
				fact.LodgerMoods = append(fact.LodgerMoods, QuestLodgerMoodFact{mood.GetPawnId(), mood.Mood})
			}
			quest.Objectives = append(quest.Objectives, fact)
		}
		seenShuttles := map[string]bool{}
		for _, shuttle := range row.Shuttles {
			if shuttle == nil || validID(shuttle.GetShuttleId()) != nil || seenShuttles[shuttle.GetShuttleId()] || shuttle.GetRequiredColonistCount() < 0 {
				return WorldProgressionRead{}, contract("invalid quest shuttle")
			}
			seenShuttles[shuttle.GetShuttleId()] = true
			for _, ids := range [][]string{shuttle.PawnIds, shuttle.LoadedPawnIds, shuttle.PendingPawnIds} {
				seen := map[string]bool{}
				for _, id := range ids {
					if validID(id) != nil || seen[id] {
						return WorldProgressionRead{}, contract("invalid quest shuttle pawn")
					}
					seen[id] = true
				}
			}
			quest.Shuttles = append(quest.Shuttles, QuestShuttleFact{ID: shuttle.GetShuttleId(), AutoloadAvailable: shuttle.AutoloadAvailable, Autoload: shuttle.Autoload, Loading: shuttle.Loading, AllRequiredLoaded: shuttle.AllRequiredLoaded, ManualLaunchAvailable: shuttle.ManualLaunchAvailable, PawnIDs: append([]string(nil), shuttle.PawnIds...), LoadedPawnIDs: append([]string(nil), shuttle.LoadedPawnIds...), RequiredColonistCount: shuttle.RequiredColonistCount})
			quest.Shuttles[len(quest.Shuttles)-1].PendingPawnIDs = append([]string(nil), shuttle.PendingPawnIds...)
		}
		for choice, amount := range favor {
			quest.Favor = append(quest.Favor, QuestFavorFact{Choice: int32(choice), Favor: amount})
		}
		sort.Slice(quest.Favor, func(a, b int) bool { return quest.Favor[a].Choice < quest.Favor[b].Choice })
		requests := make([]QuestTradeRequestFact, len(row.TradeRequests))
		for k, request := range row.TradeRequests {
			// A missing resource, count or destination on one
			// row is not malformed evidence (native's own QuestTradeRequest
			// fields are all optional), only a negative count/destination is.
			if request == nil || request.GetCount() < 0 || request.GetDestination() < 0 {
				return WorldProgressionRead{}, contract("invalid world progression quest trade request")
			}
			requests[k] = QuestTradeRequestFact{Resource: request.GetResource(), Count: request.GetCount(), DestinationTile: request.GetDestination()}
		}
		quest.TradeRequests = requests
		quests[i] = quest
	}
	factions := make([]FactionFact, 0, len(v.Factions))
	for _, row := range v.Factions {
		if row == nil || validID(row.GetId()) != nil {
			return WorldProgressionRead{}, contract("invalid world progression faction")
		}
		factions = append(factions, FactionFact{ID: row.GetId(), Player: row.GetPlayer(), Hostile: row.GetHostile()})
	}
	sites, err := validatedWorldSites(v.Sites)
	if err != nil {
		return WorldProgressionRead{}, err
	}
	assemblies, err := validatedCaravanAssemblies(v.Assemblies)
	if err != nil {
		return WorldProgressionRead{}, err
	}
	return WorldProgressionRead{Context: v.Context, Maps: maps, Caravans: rows, Quests: quests, Factions: factions, Sites: sites, Assemblies: assemblies}, nil
}
