package bridge

import (
	"context"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The definition catalog (#1340): the static planning facts of every
// buildable or sowable definition and every research project. They hold
// for a whole load, so the client reads the catalog on the first read
// under a load token and keeps it until the token changes; frames carry
// only the per-map, per-tick values (availability is derived from the
// research prerequisites and the finished projects).

const methodDefinitionCatalog = "rimgovernor/observations_read_definition_catalog"

// DefinitionCatalog is one load's decoded catalog.
type DefinitionCatalog struct {
	LoadToken string
	// Definitions are the planning rows by definition name.
	Definitions map[string]*o.PlanningDefinition
	// Research is every research project's static facts by name.
	Research map[string]policy.ResearchProjectFacts
	// Biotech is the Biotech defs (#1678); nil without Biotech.
	Biotech *BiotechCatalog
	// Odyssey is the Odyssey defs (#1708); nil without Odyssey.
	Odyssey *OdysseyCatalog
	// Anomaly is the Anomaly defs (#1737); nil without Anomaly.
	Anomaly *AnomalyCatalog
	// Ideology is the Ideology defs (#1654); nil without Ideology.
	Ideology *policy.IdeologyDefs
}

// Definition is name's catalog row, nil when the catalog has none.
func (catalog *DefinitionCatalog) Definition(name string) *o.PlanningDefinition {
	if catalog == nil {
		return nil
	}
	return catalog.Definitions[name]
}

// catalogCache holds the catalog of the newest load token read.
type catalogCache struct {
	mu      sync.Mutex
	catalog *DefinitionCatalog
}

// DefinitionCatalog is the catalog of identity's load, read over GABP the
// first time the load token is seen.
func (caller *Client) DefinitionCatalog(ctx context.Context, identity *c.Identity) (*DefinitionCatalog, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	caller.catalog.mu.Lock()
	held := caller.catalog.catalog
	caller.catalog.mu.Unlock()
	if held != nil && held.LoadToken == identity.GetLoadToken() {
		return held, nil
	}
	request := &o.DefinitionCatalogRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	reply := &o.DefinitionCatalogReply{}
	raw, err := caller.protoRead(ctx, methodDefinitionCatalog, request, reply)
	if err != nil {
		return nil, err
	}
	var catalog *DefinitionCatalog
	switch v := reply.Outcome.(type) {
	case *o.DefinitionCatalogReply_Failure:
		return nil, failure(v.Failure, raw)
	case *o.DefinitionCatalogReply_Unavailable:
		return nil, unavailable(v.Unavailable, raw)
	case *o.DefinitionCatalogReply_Observed:
		if catalog, err = DecodeDefinitionCatalog(v.Observed, identity); err != nil {
			return nil, err
		}
	default:
		return nil, contract("missing definition catalog outcome")
	}
	caller.catalog.mu.Lock()
	caller.catalog.catalog = catalog
	caller.catalog.mu.Unlock()
	return catalog, nil
}

// DecodeDefinitionCatalog validates a catalog read under identity.
func DecodeDefinitionCatalog(v *o.DefinitionCatalog, identity *c.Identity) (*DefinitionCatalog, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return nil, contract("invalid definition catalog context")
	}
	if err := buildingUnknown(v); err != nil {
		return nil, err
	}
	out := &DefinitionCatalog{LoadToken: identity.GetLoadToken(), Definitions: make(map[string]*o.PlanningDefinition, len(v.Definitions)), Research: make(map[string]policy.ResearchProjectFacts, len(v.Research))}
	var err error
	if out.Biotech, err = DecodeBiotechCatalog(v.Biotech); err != nil {
		return nil, err
	}
	if out.Odyssey, err = DecodeOdysseyCatalog(v.Odyssey); err != nil {
		return nil, err
	}
	if out.Anomaly, err = DecodeAnomalyCatalog(v.Anomaly); err != nil {
		return nil, err
	}
	for _, row := range v.Definitions {
		if err := validatePlanningDefinition(row); err != nil {
			return nil, err
		}
		name := row.Definition.GetDefName()
		if out.Definitions[name] != nil {
			return nil, contract("duplicate catalog definition %s", name)
		}
		out.Definitions[name] = row
	}
	for _, row := range v.Research {
		if row == nil || row.Project == nil || validID(row.Project.GetDefName()) != nil {
			return nil, contract("invalid catalog research project")
		}
		name := row.Project.GetDefName()
		if _, exists := out.Research[name]; exists {
			return nil, contract("duplicate catalog research project %s", name)
		}
		for _, list := range [][]string{row.Prerequisites, row.HiddenPrerequisites} {
			for _, prerequisite := range list {
				if validID(prerequisite) != nil {
					return nil, contract("invalid catalog research prerequisite")
				}
			}
		}
		// The catalog does not carry native ResearchProjectDef.hidden (an
		// anomaly codex state, not a static fact): every project is treated
		// as not hidden, and a category-bound (anomaly) project is refused
		// as an ordinary prerequisite by its category instead.
		// A project with no prerequisites is an absent repeated field on
		// the wire, which is known-empty, not unread.
		out.Research[name] = policy.ResearchProjectFacts{Name: policy.ResearchProjectID(name), Hidden: domain.Known(false), KnowledgeCategory: row.GetCategory(),
			Prerequisites: domain.Known(toProjectIDs(row.GetPrerequisites())), HiddenPrerequisites: domain.Known(toProjectIDs(row.GetHiddenPrerequisites())), RequiredBuilding: row.GetRequiredBuilding()}
	}
	if out.Ideology, err = DecodeIdeologyCatalog(v.Ideology); err != nil {
		return nil, err
	}
	return out, nil
}
