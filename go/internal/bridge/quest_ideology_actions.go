package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func hackDesignationAction(action domain.Action) (*o.Action, error) {
	h, ok := action.HackDesignation()
	if !ok {
		return nil, contract("not a hack designation action")
	}
	if _, err := domain.NewHackDesignationAction(action.ID(), h); err != nil {
		return nil, contract("invalid hack designation")
	}
	return &o.Action{Intent: &o.Action_HackDesignation{HackDesignation: &o.HackDesignationIntent{Target: NewRef(h.Target()), Enabled: proto.Bool(h.Enabled())}}}, nil
}

func giveItemAction(action domain.Action) (*o.Action, error) {
	g, ok := action.GiveItem()
	if !ok {
		return nil, contract("not a give item action")
	}
	if _, err := domain.NewGiveItemAction(action.ID(), g); err != nil {
		return nil, contract("invalid give item")
	}
	return &o.Action{Intent: &o.Action_GiveItem{GiveItem: &o.GiveItemIntent{Hauler: NewRef(string(g.Hauler())), Recipient: NewRef(string(g.Recipient())), Definition: proto.String(g.Definition()), ExpectedRemaining: proto.Int64(g.ExpectedRemaining())}}}, nil
}
