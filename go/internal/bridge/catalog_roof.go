package bridge

import (
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The roof rule as a Go view of the def mirror:
// RoofDef.isThickRoof decides whether a roof can be removed and isNatural
// whether it is a mountain roof. Nothing is read
// from native and no roof name is listed here.

// ErrRoofRules marks a catalog that carries no RoofDef rows.
var ErrRoofRules = errors.New("roof rules")

// RoofRules is every RoofDef row of the catalog as policy rules. A catalog
// with no RoofDef rows is an error wrapping ErrRoofRules: every game has at
// least its own roofs, so none means the mirror was not read.
func (catalog *DefinitionCatalog) RoofRules() (policy.RoofRules, error) {
	if catalog == nil {
		return nil, fmt.Errorf("%w: no definition catalog", ErrRoofRules)
	}
	out := policy.RoofRules{}
	for name, row := range catalog.Defs[(&d.RoofDef{}).ProtoReflect().Descriptor().FullName()] {
		roof, ok := row.(*d.RoofDef)
		if !ok {
			return nil, fmt.Errorf("%w: row %s is not a RoofDef", ErrRoofRules, name)
		}
		out[name] = policy.RoofRule{Thick: roof.GetIsThickRoof(), Natural: roof.GetIsNatural()}
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("%w: the catalog has no RoofDef rows", ErrRoofRules)
	}
	return out, nil
}
