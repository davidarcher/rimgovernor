package bridge

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The creepjoiner planner's def lookups, all in this file: each reads
// the def mirror (DefinitionCatalog.Defs) and names no def.

// surgicalInspectionWorker is the worker class of the game's surgical
// inspection recipe (RecipeDef.workerClass), the mirror's type name.
const surgicalInspectionWorker = "Recipe_SurgicalInspection"

// SurgicalInspectionRecipes are the recipe defs whose worker is the game's
// surgical inspection; none without a catalog or without Anomaly.
func (catalog *DefinitionCatalog) SurgicalInspectionRecipes() policy.InspectionRecipes {
	out := policy.InspectionRecipes{}
	if catalog == nil {
		return out
	}
	for name, row := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		recipe, ok := row.(*d.RecipeDef)
		if !ok {
			continue
		}
		worker := recipe.GetWorkerClass()
		if worker == surgicalInspectionWorker || strings.HasSuffix(worker, "."+surgicalInspectionWorker) {
			out[name] = true
		}
	}
	return out
}
