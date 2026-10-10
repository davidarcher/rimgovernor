package stateval

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// Catalog is what the evaluator reads of the decoded definition catalog. The
// bridge's DefinitionCatalog satisfies it; stateval imports no bridge code, so
// the bridge can call the evaluator directly.
type Catalog interface {
	// ThingDef and TerrainDef are the def's generated row, nil when absent.
	ThingDef(name string) *d.ThingDef
	TerrainDef(name string) *d.TerrainDef
	// AllThingDefs are every ThingDef row by defName; callers do not mutate it.
	AllThingDefs() map[string]*d.ThingDef
	// Rows are the rows of one other concrete Def class (a defs.proto message,
	// by full name) by defName.
	Rows(class protoreflect.FullName) map[string]proto.Message
	// ClassIsA is typeof(base).IsAssignableFrom(class) over the CLR class chains.
	ClassIsA(class, base string) (bool, error)
	GameConstants() (*d.GameConstants, error)
	// FilterAccepts is whether a ThingFilter allows the def.
	FilterAccepts(filter *d.ThingFilter, def string) (bool, error)
	// RecipeIngredients are a recipe's ingredient slots as alternatives.
	RecipeIngredients(name string) (domain.Fact[[][]policy.Amount], error)
}

// DefRow is name's generated row of def class T (a message of defs.proto other
// than ThingDef and TerrainDef), nil when the catalog has none.
func DefRow[T proto.Message](catalog Catalog, name string) T {
	var zero T
	if catalog == nil {
		return zero
	}
	row, _ := catalog.Rows(zero.ProtoReflect().Descriptor().FullName())[name].(T)
	return row
}

// NotMirrored is the error for a fact the game computes in code that the
// mirror does not carry: the evaluator returns it for a StatWorker or
// StatPart class that cmd/stataudit lists as unowned, never a default value.
type NotMirrored struct {
	Class string // the StatWorker or StatPart class
	Fact  string // what the class computes that the mirror lacks
}

func (e *NotMirrored) Error() string {
	return "stat class " + e.Class + ": " + e.Fact + " is computed in game code and not mirrored"
}
