package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// SettlementGiftTarget is the fresh CAS evidence InspectSettlementGift needs
// immediately before preview: one already-selected caravan's exact position
// and crew (self-tokened the same way native's CaravanToken does, from the
// same world-progression census CaravanJourneyTracker already reads), plus
// the exact settlement sitting at that caravan's tile and its faction's
// current relation (native-tokened, freshly read via ReadWorld — this is the
// actual freshness gate on goodwill/hostility Go cannot compute itself). No
// dedicated candidate search is needed to dispatch one already-selected
// caravan visiting one already-arrived-at settlement.
type SettlementGiftTarget struct {
	Context *c.ObservationContext

	Caravan       string
	CaravanToken  string
	CaravanTile   int32
	CaravanMoving bool
	CrewIDs       []string
	Silver        int32

	Settlement    string
	SettlementLabel string
	FactionID     string
	FactionDefName string
	FactionToken  string
	Relation      string
	Goodwill      int32
	GoodwillKnown bool
	Player        bool
}

// ReadSettlementGiftTarget reads the world-progression census for one
// already-selected caravan, then reads the world census for the settlement
// (if any) sitting at that caravan's exact current tile. It requires the
// same single complete page both ReadWorldProgression and ReadWorld already
// enforce; storage is never requested since gift facts do not depend on it.
func (client *Client) ReadSettlementGiftTarget(ctx context.Context, identity *c.Identity, caravan string) (SettlementGiftTarget, Result, error) {
	if validID(caravan) != nil {
		return SettlementGiftTarget{}, Result{}, contract("invalid settlement gift target identity")
	}
	identity = proto.Clone(identity).(*c.Identity)
	journey, raw, err := client.ReadWorldProgression(ctx, identity, false)
	if err != nil {
		return SettlementGiftTarget{}, raw, err
	}
	var caravanRow *CaravanJourney
	for i := range journey.Caravans {
		if journey.Caravans[i].ID != caravan {
			continue
		}
		if caravanRow != nil {
			return SettlementGiftTarget{}, raw, contract("duplicate world progression caravan")
		}
		caravanRow = &journey.Caravans[i]
	}
	if caravanRow == nil {
		return SettlementGiftTarget{}, raw, ErrUnavailable
	}
	if caravanRow.Moving {
		return SettlementGiftTarget{}, raw, ErrUnavailable
	}

	world, rawWorld, err := client.ReadWorld(ctx, identity, caravanRow.Tile, 0)
	if err != nil {
		return SettlementGiftTarget{}, rawWorld, err
	}
	var settlementRow *SettlementFact
	for i := range world.Settlements {
		if world.Settlements[i].Tile != caravanRow.Tile {
			continue
		}
		if settlementRow != nil {
			return SettlementGiftTarget{}, rawWorld, contract("duplicate world settlement at caravan tile")
		}
		settlementRow = &world.Settlements[i]
	}
	if settlementRow == nil || settlementRow.FactionID == "" || settlementRow.Player {
		return SettlementGiftTarget{}, rawWorld, ErrUnavailable
	}

	return SettlementGiftTarget{
		Context: journey.Context,

		Caravan:       caravanRow.ID,
		CaravanToken:  caravanToken(caravanRow.ID, caravanRow.Tile, caravanRow.Moving, caravanRow.PawnIDs),
		CaravanTile:   caravanRow.Tile,
		CaravanMoving: caravanRow.Moving,
		CrewIDs:       append([]string(nil), caravanRow.PawnIDs...),
		Silver:        caravanRow.Silver,

		Settlement:      settlementRow.ID,
		SettlementLabel: settlementRow.Label,
		FactionID:       settlementRow.FactionID,
		FactionDefName:  settlementRow.FactionDefName,
		FactionToken:    settlementRow.FactionSnapshotToken,
		Relation:        settlementRow.Relation,
		Goodwill:        settlementRow.Goodwill,
		GoodwillKnown:   settlementRow.GoodwillKnown,
		Player:          settlementRow.Player,
	}, rawWorld, nil
}
