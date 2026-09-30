package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// autoHomeAreaAction is the AutoHomeAreaIntent that sets the game's
// home-area auto-expand (#1322). A value that already holds applies again.
func autoHomeAreaAction(action domain.Action) (*o.Action, error) {
	enabled, ok := action.AutoHomeArea()
	if !ok {
		return nil, contract("not an auto home area action")
	}
	return &o.Action{Intent: &o.Action_AutoHomeArea{AutoHomeArea: &o.AutoHomeAreaIntent{Enabled: proto.Bool(enabled)}}}, nil
}
