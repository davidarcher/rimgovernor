package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func (caller *Client) CreationCatalog(ctx context.Context) (*DefinitionCatalog, Result, error) {
	reply := &o.DefinitionCatalogReply{}
	raw, err := caller.protoCall(ctx, methodDefinitionCatalog, &o.DefinitionCatalogRequest{Creation: proto.Bool(true)}, reply)
	if err != nil {
		return nil, raw, err
	}
	switch outcome := reply.Outcome.(type) {
	case *o.DefinitionCatalogReply_Failure:
		return nil, raw, failure(outcome.Failure, raw)
	case *o.DefinitionCatalogReply_Unavailable:
		return nil, raw, contract("creation catalog unavailable: %s", outcome.Unavailable.GetDetail())
	case *o.DefinitionCatalogReply_Creation:
		catalog, err := DecodeCreationCatalog(outcome.Creation)
		return catalog, raw, err
	default:
		return nil, raw, contract("creation catalog outcome missing")
	}
}

func DecodeCreationCatalog(v *o.CreationDefinitionCatalog) (*DefinitionCatalog, error) {
	if v == nil {
		return nil, contract("creation catalog missing")
	}
	rows, err := defSetRows(v.Defs)
	if err != nil {
		return nil, err
	}
	bases, err := classBases(v.ClassChains)
	if err != nil {
		return nil, err
	}
	return &DefinitionCatalog{Defs: rows, classBases: bases, gameConstants: v.GameConstants}, nil
}

func (catalog *DefinitionCatalog) StartingIdeoligion(scenario string) (policy.DesignChoice, error) {
	row := DefRow[*d.ScenarioDef](catalog, scenario)
	if row == nil || row.GetScenario().GetPlayerFaction().GetFactionDef() == "" {
		return policy.DesignChoice{}, contract("creation scenario lacks its player faction")
	}
	options, err := catalog.IdeoligionOptions(row.GetScenario().GetPlayerFaction().GetFactionDef())
	if err != nil {
		return policy.DesignChoice{}, err
	}
	choice, known := policy.ChooseStartingIdeoligion(options).Value()
	if !known {
		return policy.DesignChoice{}, contract("no supported legal ideoligion design can be established")
	}
	return choice, nil
}
