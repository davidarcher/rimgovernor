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

// HasAnomaly is whether the game runs Anomaly: the def mirror carries
// EntityCategoryDef rows only with the expansion. False for a nil catalog.
func (catalog *DefinitionCatalog) HasAnomaly() bool {
	return catalog != nil && len(catalog.Defs[(&d.EntityCategoryDef{}).ProtoReflect().Descriptor().FullName()]) > 0
}

// CreepJoinerDownsides is what the creepjoiner downside defs add: the trait
// and hediff def names over every CreepJoinerDownsideDef row. A nil catalog or
// a game without Anomaly adds none.
func (catalog *DefinitionCatalog) CreepJoinerDownsides() policy.CreepJoinerDownsides {
	out := policy.CreepJoinerDownsides{Traits: map[string]bool{}, Hediffs: map[string]bool{}}
	if catalog == nil {
		return out
	}
	for _, row := range catalog.Defs[(&d.CreepJoinerDownsideDef{}).ProtoReflect().Descriptor().FullName()] {
		def, ok := row.(*d.CreepJoinerDownsideDef)
		if !ok {
			continue
		}
		for _, t := range def.GetTraits() {
			out.Traits[t.GetValue().GetDef()] = true
		}
		for _, h := range def.GetHediffs() {
			out.Hediffs[h] = true
		}
	}
	return out
}
