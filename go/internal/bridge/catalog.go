package bridge

import (
	"context"
	"math"
	"slices"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
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
	// ThingDefs and TerrainDefs are every def with all its fields (#1730)
	// by defName: the generated messages of defs.proto, unfiltered.
	ThingDefs   map[string]*d.ThingDef
	TerrainDefs map[string]*d.TerrainDef
	// Constants are the game constants the native read took from the game
	// assemblies.
	Constants *o.CatalogConstants
}

// ThingDef is name's generated def row, nil when the catalog has none.
func (catalog *DefinitionCatalog) ThingDef(name string) *d.ThingDef {
	if catalog == nil {
		return nil
	}
	return catalog.ThingDefs[name]
}

// TerrainDef is name's generated def row, nil when the catalog has none.
func (catalog *DefinitionCatalog) TerrainDef(name string) *d.TerrainDef {
	if catalog == nil {
		return nil
	}
	return catalog.TerrainDefs[name]
}

// Definition is name's catalog row, nil when the catalog has none.
func (catalog *DefinitionCatalog) Definition(name string) *o.PlanningDefinition {
	if catalog == nil {
		return nil
	}
	return catalog.Definitions[name]
}

// RoomRoleDefinitions are the names of the definitions the native catalog
// assigns a room-role furniture role, sorted.
func (catalog *DefinitionCatalog) RoomRoleDefinitions() []string {
	if catalog == nil {
		return nil
	}
	var names []string
	for name, row := range catalog.Definitions {
		if len(row.RoomRoles) > 0 {
			names = append(names, name)
		}
	}
	slices.Sort(names)
	return names
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

// defRows keys generated def rows by defName; no rows, a row without a
// valid name or a repeated name is a contract violation (the game defines
// both kinds, so an empty list is a reply from a build without the def
// mirror).
func defRows[T any](rows []*T, name func(*T) string, kind string) (map[string]*T, error) {
	if len(rows) == 0 {
		return nil, contract("catalog carries no %s defs", kind)
	}
	out := make(map[string]*T, len(rows))
	for _, row := range rows {
		if row == nil || validID(name(row)) != nil {
			return nil, contract("invalid catalog %s def", kind)
		}
		if out[name(row)] != nil {
			return nil, contract("duplicate catalog %s def %s", kind, name(row))
		}
		out[name(row)] = row
	}
	return out, nil
}

// validateConstants refuses an absent constants block and one with a
// non-positive or non-finite value.
func validateConstants(v *o.CatalogConstants) (*o.CatalogConstants, error) {
	if v == nil {
		return nil, contract("catalog carries no constants")
	}
	for name, n := range map[string]int32{"ticks_per_hour": v.TicksPerHour, "ticks_per_day": v.TicksPerDay, "days_per_year": v.DaysPerYear, "bill_stack_max": v.BillStackMax, "skill_max_level": v.SkillMaxLevel} {
		if n <= 0 {
			return nil, contract("catalog constant %s is %d", name, n)
		}
	}
	if g := float64(v.LitGlowThreshold); math.IsNaN(g) || math.IsInf(g, 0) || g <= 0 {
		return nil, contract("catalog constant lit_glow_threshold is %v", g)
	}
	return v, nil
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
	if out.ThingDefs, err = defRows(v.ThingDefs, (*d.ThingDef).GetDefName, "thing"); err != nil {
		return nil, err
	}
	if out.TerrainDefs, err = defRows(v.TerrainDefs, (*d.TerrainDef).GetDefName, "terrain"); err != nil {
		return nil, err
	}
	if out.Constants, err = validateConstants(v.Constants); err != nil {
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
