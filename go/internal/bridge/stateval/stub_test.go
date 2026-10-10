package stateval

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// stubCatalog is a Catalog over a recorded wire message. The bridge's decoded
// catalog implements Catalog too, but bridge imports this package, so these
// unit tests cannot build one; the stub holds the rows and class chains, and
// the two recipe lookups (FilterAccepts, RecipeIngredients) are refused: the
// tests that need them live in the bridge package's evaluator tests.
type stubCatalog struct {
	things   map[string]*d.ThingDef
	terrains map[string]*d.TerrainDef
	defs     map[protoreflect.FullName]map[string]proto.Message
	bases    map[string][]string
	game     *d.GameConstants
}

func fromWire(wire *o.DefinitionCatalog) *stubCatalog {
	c := &stubCatalog{
		things: map[string]*d.ThingDef{}, terrains: map[string]*d.TerrainDef{},
		defs: map[protoreflect.FullName]map[string]proto.Message{}, bases: map[string][]string{}, game: wire.GameConstants,
	}
	for _, row := range wire.ThingDefs {
		c.things[row.GetDefName()] = row
	}
	for _, row := range wire.TerrainDefs {
		c.terrains[row.GetDefName()] = row
	}
	for _, chain := range wire.ClassChains {
		c.bases[chain.GetName()] = chain.GetBases()
	}
	sets := wire.Defs.ProtoReflect()
	fields := sets.Descriptor().Fields()
	for i := range fields.Len() {
		field := fields.Get(i)
		list := sets.Get(field).List()
		rows := map[string]proto.Message{}
		for j := range list.Len() {
			row := list.Get(j).Message()
			rows[row.Get(row.Descriptor().Fields().ByName("defName")).String()] = row.Interface()
		}
		c.defs[field.Message().FullName()] = rows
	}
	return c
}

// recordedStub is the stub of the whole recorded catalog.
func recordedStub(t testing.TB) *stubCatalog { return fromWire(testkit.RecordedCatalogWire(t)) }

// recordedEnv is the stat environment the recording carries.
func recordedEnv(t testing.TB) Env {
	t.Helper()
	wire := testkit.RecordedCatalogWire(t).GetStatEnv()
	flags := map[string]bool{}
	for _, f := range wire.GetDifficultyFlags() {
		flags[f.GetName()] = f.GetValue()
	}
	env := Env{ActiveMods: map[string]bool{}, ClassicMode: wire.GetClassicMode(), ScenarioFactors: map[string]float32{},
		Difficulty: Some(Difficulty{ButcherYieldFactor: wire.GetButcherYieldFactor(), FishingYieldFactor: wire.GetFishingYieldFactor(), Flags: flags})}
	for _, mod := range wire.GetActiveMods() {
		env.ActiveMods[mod] = true
	}
	for _, f := range wire.GetScenarioFactors() {
		env.ScenarioFactors[f.GetStat()] = f.GetFactor()
	}
	return env
}

func (c *stubCatalog) ThingDef(name string) *d.ThingDef     { return c.things[name] }
func (c *stubCatalog) TerrainDef(name string) *d.TerrainDef { return c.terrains[name] }
func (c *stubCatalog) AllThingDefs() map[string]*d.ThingDef { return c.things }
func (c *stubCatalog) Rows(class protoreflect.FullName) map[string]proto.Message {
	return c.defs[class]
}

func (c *stubCatalog) ClassIsA(class, base string) (bool, error) {
	bases, ok := c.bases[class]
	if !ok {
		return false, fmt.Errorf("catalog has no class chain for %s", class)
	}
	if class == base {
		return true, nil
	}
	for _, b := range bases {
		if b == base {
			return true, nil
		}
	}
	return false, nil
}

func (c *stubCatalog) GameConstants() (*d.GameConstants, error) {
	if c.game == nil {
		return nil, fmt.Errorf("catalog carries no game constants")
	}
	return c.game, nil
}

func (c *stubCatalog) FilterAccepts(*d.ThingFilter, string) (bool, error) {
	return false, fmt.Errorf("the stub catalog does not resolve thing filters")
}

func (c *stubCatalog) RecipeIngredients(string) (domain.Fact[[][]policy.Amount], error) {
	return domain.Unknown[[][]policy.Amount](), fmt.Errorf("the stub catalog does not resolve recipe ingredients")
}

// stub is the evaluator's catalog as the stub the unit tests build.
func (e *Evaluator) stub() *stubCatalog { return e.catalog.(*stubCatalog) }
