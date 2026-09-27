package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// namingAction is the NamingIntent of one naming confirmation: the exact
// observed window and suggestions of the colony-wide initial naming dialog.
func namingAction(action domain.Action) (*o.Action, error) {
	v, ok := action.NamingConfirmation()
	if !ok {
		return nil, contract("not a naming confirmation action")
	}
	if v.WindowID() < 0 || validID(v.FactionName()) != nil || validID(v.SettlementName()) != nil {
		return nil, contract("invalid naming target")
	}
	return namingIntent(v.WindowID(), v.FactionName(), v.SettlementName()), nil
}

func namingIntent(windowID int32, factionName, settlementName string) *o.Action {
	return &o.Action{Intent: &o.Action_Naming{Naming: &o.NamingIntent{
		WindowId: proto.Int32(windowID), FactionName: proto.String(factionName), SettlementName: proto.String(settlementName)}}}
}
