package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// QuestFulfillTarget is the fresh CAS evidence InspectQuestFulfill needs
// immediately before preview: one already-accepted quest's exact settled
// state and trade-request destination tile, plus the exact already-visiting
// caravan's position and crew (self-tokened the same way
// NativeSettlementGiftOperations.CaravanToken does, from the same
// world-progression census both this and CaravanJourneyTracker already
// read). No dedicated candidate search is needed to dispatch one
// already-selected quest/caravan pair; native alone decides whether the
// live TradeRequestComp resource/count still match at admission time.
type QuestFulfillTarget struct {
	Context *c.ObservationContext

	Quest           string
	QuestToken      string
	State           string
	HasTradeRequest bool

	Caravan       string
	CaravanToken  string
	CaravanTile   int32
	CaravanMoving bool
	CrewIDs       []string

	AtTarget bool
}

// ReadQuestFulfillTarget reads the whole world-progression census via
// ReadWorldProgression and extracts one already-selected quest and
// already-selected caravan's fresh facts. It requires the same single
// complete page ReadWorldProgression already enforces; storage is never
// requested since fulfillment facts do not depend on it.
func (client *Client) ReadQuestFulfillTarget(ctx context.Context, identity *c.Identity, quest, caravan string) (QuestFulfillTarget, Result, error) {
	if validID(quest) != nil || validID(caravan) != nil {
		return QuestFulfillTarget{}, Result{}, contract("invalid quest fulfill target identity")
	}
	identity = proto.Clone(identity).(*c.Identity)
	read, raw, err := client.ReadWorldProgression(ctx, identity, false)
	if err != nil {
		return QuestFulfillTarget{}, raw, err
	}
	var questRow *QuestOffer
	for i := range read.Quests {
		if read.Quests[i].ID != quest {
			continue
		}
		if questRow != nil {
			return QuestFulfillTarget{}, raw, contract("duplicate world progression quest")
		}
		questRow = &read.Quests[i]
	}
	if questRow == nil {
		return QuestFulfillTarget{}, raw, ErrUnavailable
	}
	var caravanRow *CaravanJourney
	for i := range read.Caravans {
		if read.Caravans[i].ID != caravan {
			continue
		}
		if caravanRow != nil {
			return QuestFulfillTarget{}, raw, contract("duplicate world progression caravan")
		}
		caravanRow = &read.Caravans[i]
	}
	if caravanRow == nil {
		return QuestFulfillTarget{}, raw, ErrUnavailable
	}
	atTarget := questRow.TradeDestinationKnown && !caravanRow.Moving && caravanRow.Tile == questRow.TradeDestinationTile
	return QuestFulfillTarget{
		Context: read.Context,

		Quest: questRow.ID, QuestToken: questRow.SnapshotToken, State: questRow.State, HasTradeRequest: questRow.HasTradeRequest,

		Caravan:       caravanRow.ID,
		CaravanToken:  questFulfillCaravanToken(caravanRow.ID, caravanRow.Tile, caravanRow.Moving, caravanRow.PawnIDs),
		CaravanTile:   caravanRow.Tile,
		CaravanMoving: caravanRow.Moving,
		CrewIDs:       append([]string(nil), caravanRow.PawnIDs...),

		AtTarget: atTarget,
	}, raw, nil
}
