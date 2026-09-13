package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// CaravanJourney is the validated subset of one player caravan's world-progression
// census row that travel/arrival tracking needs: its native identity, whether
// it is still moving, and the crew currently aboard. It exists so a boundary
// can tell "still travelling" from "no longer a caravan" (the World Object
// disappears once its pawns enter any map, home or foreign) without depending
// on FormCaravan's own attempt/receipt machinery, which only ever reports
// "did native form and start this caravan" (see NativeCaravanRecord's doc
// comment). This type does not surface CaravanState.pawns' full PawnState,
// home_routes or mass/food fields; a future slice adds those once Go's
// return-storage/failure-recovery workflow needs them. Silver is the one
// inventory quantity settlement-gift admission needs (a conservative reserve
// check, not a full cargo manifest); native always populates the full
// inventory census (Inventory() has no failure path), so an absent Silver
// row is a known zero, not unknown.
type CaravanJourney struct {
	ID      string
	Tile    int32
	Moving  bool
	PawnIDs []string
	Silver  int32
}

// QuestOffer is the validated subset of one WorldProgressionSnapshot.quests
// row (NativeWorldProgressionObservation.cs's Quests()) that the quest-accept
// boundary needs: identity, native settled state, whether an accepter pawn is
// required and which of the currently eligible colonists may supply it,
// native's own CanAcceptQuest verdict, the exact count of options in the
// quest's single native reward-choice part (0 when it carries none, so a
// caller never has to guess), and whether it also carries a settlement trade
// objective (FulfillQuest territory, out of scope for acceptance). It does
// not surface reward item contents or trade destination; nothing here picks
// a quest or a reward, it only proves facts about one already-selected quest.
type QuestOffer struct {
	ID               string
	State            string
	RequiresAccepter bool
	CanAccept        bool
	ChoiceCount      int32
	HasTradeRequest  bool
	EligiblePawnIDs  []string
	SnapshotToken    string
}

// WorldMap is the validated subset of one WorldProgressionSnapshot.maps row
// that failure-recovery classification needs: which map this is (native
// uniqueID) and whether native marks it a player home map, plus the pawn
// IDs native reports currently spawned there. It does not surface tile,
// label or stored items; nothing here reads or writes anything on a
// foreign map, it only lets a caller tell "this pawn is alive and visible
// on some live map" from "absent from every census we can read".
type WorldMap struct {
	ID      int32
	Home    bool
	PawnIDs []string
}

// WorldProgressionRead is the validated subset of one rimgovernor/
// observations_read_world_progression census this round's caravan-journey
// tracking and quest-accept boundary need. It does not surface
// WorldProgressionSnapshot.factions or assemblies; those remain unread
// until a later slice needs them, the same "read only what a boundary can
// validate and use" discipline as CaravanCatalogRead. Maps is read only for
// its pawn rosters (see WorldMap): a caravan whose world object has
// disappeared but whose crew is visible on some non-home map is known to be
// on a live map (an encounter/ambush map, most likely), not lost -- without
// ever claiming custody of that map's pawns or issuing any write to it.
type WorldProgressionRead struct {
	Context  *c.ObservationContext
	Maps     []WorldMap
	Caravans []CaravanJourney
	Quests   []QuestOffer
}

// ReadWorldProgression reads native's world progression census. As of this
// writing native implements this handler (NativeWorldProgressionObservation.cs,
// ported from the legacy home/world_progression JSON tool); this wrapper is
// the first Go consumer of it.
func (client *Client) ReadWorldProgression(ctx context.Context, identity *c.Identity, includeStorage bool) (WorldProgressionRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return WorldProgressionRead{}, Result{}, err
	}
	request := &o.WorldProgressionRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, IncludeStorage: proto.Bool(includeStorage), Page: &c.PageRequest{Limit: proto.Uint32(256)}}
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
	counts := v.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" {
		return WorldProgressionRead{}, contract("incomplete world progression page")
	}
	if len(v.Maps) > 256 {
		return WorldProgressionRead{}, contract("world progression maps exceed bound")
	}
	maps := make([]WorldMap, len(v.Maps))
	seenMapPawns := map[string]bool{}
	for i, row := range v.Maps {
		if row == nil || row.Id == nil || row.Home == nil || len(row.Pawns) > 256 {
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
		maps[i] = WorldMap{ID: row.GetId(), Home: row.GetHome(), PawnIDs: pawnIDs}
	}
	if len(v.Caravans) > 256 {
		return WorldProgressionRead{}, contract("world progression caravans exceed bound")
	}
	seen := map[string]bool{}
	rows := make([]CaravanJourney, len(v.Caravans))
	for i, row := range v.Caravans {
		if row == nil || row.Caravan == nil || validID(row.Caravan.GetId()) != nil || seen[row.Caravan.GetId()] {
			return WorldProgressionRead{}, contract("invalid or duplicate world progression caravan")
		}
		seen[row.Caravan.GetId()] = true
		if row.Tile == nil || row.GetTile() < 0 || len(row.Pawns) > 64 {
			return WorldProgressionRead{}, contract("invalid world progression caravan tile or crew")
		}
		pawnIDs := make([]string, len(row.Pawns))
		seenPawns := map[string]bool{}
		for j, pawn := range row.Pawns {
			if pawn == nil || pawn.Pawn == nil || validID(pawn.Pawn.GetId()) != nil || seenPawns[pawn.Pawn.GetId()] {
				return WorldProgressionRead{}, contract("invalid or duplicate world progression caravan pawn")
			}
			seenPawns[pawn.Pawn.GetId()] = true
			pawnIDs[j] = pawn.Pawn.GetId()
		}
		if len(row.Inventory) > 4096 {
			return WorldProgressionRead{}, contract("world progression caravan inventory exceeds bound")
		}
		var silver int32
		seenDefs := map[string]bool{}
		for _, item := range row.Inventory {
			if item == nil || item.DefName == nil || seenDefs[item.GetDefName()] {
				return WorldProgressionRead{}, contract("invalid or duplicate world progression caravan inventory row")
			}
			seenDefs[item.GetDefName()] = true
			if item.GetDefName() == "Silver" {
				if item.Units == nil || item.GetUnits() < 0 || item.GetUnits() > math.MaxInt32 {
					return WorldProgressionRead{}, contract("invalid world progression caravan silver")
				}
				silver = int32(item.GetUnits())
			}
		}
		rows[i] = CaravanJourney{ID: row.Caravan.GetId(), Tile: row.GetTile(), Moving: row.GetMoving(), PawnIDs: pawnIDs, Silver: silver}
	}
	if len(v.Quests) > 256 {
		return WorldProgressionRead{}, contract("world progression quests exceed bound")
	}
	seenQuests := map[string]bool{}
	quests := make([]QuestOffer, len(v.Quests))
	for i, row := range v.Quests {
		if row == nil || validID(row.GetId()) != nil || seenQuests[row.GetId()] || row.State == nil || row.RequiresAccepter == nil || row.CanAccept == nil || len(row.EligiblePawns) > 64 || len(row.Rewards) > 256 || len(row.TradeRequests) > 16 {
			return WorldProgressionRead{}, contract("invalid or duplicate world progression quest")
		}
		seenQuests[row.GetId()] = true
		if row.Snapshot == nil || row.Snapshot.GetEntityId() != row.GetId() || validID(row.Snapshot.GetToken()) != nil {
			return WorldProgressionRead{}, contract("world progression quest CAS token unavailable")
		}
		choices := map[uint32]bool{}
		for _, reward := range row.Rewards {
			if reward == nil {
				return WorldProgressionRead{}, contract("invalid world progression quest reward")
			}
			choices[reward.GetChoiceIndex()] = true
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
		quests[i] = QuestOffer{
			ID: row.GetId(), State: row.GetState(), RequiresAccepter: row.GetRequiresAccepter(), CanAccept: row.GetCanAccept(),
			ChoiceCount: int32(len(choices)), HasTradeRequest: len(row.TradeRequests) > 0, EligiblePawnIDs: pawnIDs, SnapshotToken: row.Snapshot.GetToken(),
		}
	}
	return WorldProgressionRead{Context: v.Context, Maps: maps, Caravans: rows, Quests: quests}, nil
}
