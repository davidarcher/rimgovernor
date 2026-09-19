package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// QuestTarget is the fresh quest CAS evidence InspectQuestAccept needs
// immediately before preview: one already-selected quest's exact settled
// state, reward-choice option count and eligible-accepter roster, read from
// the same world-progression census CaravanJourneyTracker already reads. No
// dedicated candidate search is needed to dispatch one already-selected
// quest, the same as ReadPrisonerInteractionTarget/ReadHusbandryTarget.
type QuestTarget struct {
	Context          *c.ObservationContext
	Quest            string
	SnapshotToken    string
	State            string
	RequiresAccepter bool
	CanAccept        bool
	ChoiceCount      int32
	HasTradeRequest  bool
	EligiblePawnIDs  []string
}

// ReadQuestAcceptTarget reads the whole world-progression census via
// ReadWorldProgression and extracts one already-selected quest's fresh CAS
// token and eligibility facts. It requires the same single complete page
// ReadWorldProgression already enforces; storage is never requested since
// quest facts do not depend on it.
func (client *Client) ReadQuestAcceptTarget(ctx context.Context, identity *c.Identity, quest string) (QuestTarget, Result, error) {
	if validID(quest) != nil {
		return QuestTarget{}, Result{}, contract("invalid quest accept target identity")
	}
	identity = proto.Clone(identity).(*c.Identity)
	read, raw, err := client.ReadWorldProgression(ctx, identity, false)
	if err != nil {
		return QuestTarget{}, raw, err
	}
	var row *QuestOffer
	for i := range read.Quests {
		if read.Quests[i].ID != quest {
			continue
		}
		if row != nil {
			return QuestTarget{}, raw, contract("duplicate world progression quest")
		}
		row = &read.Quests[i]
	}
	if row == nil {
		return QuestTarget{}, raw, ErrUnavailable
	}
	return QuestTarget{
		Context: read.Context, Quest: row.ID, SnapshotToken: row.SnapshotToken, State: row.State,
		RequiresAccepter: row.RequiresAccepter, CanAccept: row.CanAccept, ChoiceCount: row.ChoiceCount,
		HasTradeRequest: row.HasTradeRequest, EligiblePawnIDs: append([]string(nil), row.EligiblePawnIDs...),
	}, raw, nil
}
