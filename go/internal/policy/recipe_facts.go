package policy

import (
	"slices"
)

// RecipeFacts are what the catalog's RecipeDef rows say about the recipes the
// planners treat specially. Names are the recipes' defNames; every
// fact is derived from the rows, never from a name.
type RecipeFacts struct {
	// MaterialInstalls are the install recipes of parts made straight from a
	// stuff (a peg leg from a log), by recipe.
	MaterialInstalls []MaterialInstall
	// BillWork is the work type a bill on the recipe puts a pawn to, by
	// recipe: that of the first bench (by name) whose DoBill giver serves it.
	// A recipe no bench hosts has none.
	BillWork map[string]WorkType
	// HarvestOrgans are the paired vital organs a harvest takes, by body part
	// defName (each spawns the item of the same defName): a vital part whose
	// body carries two, so one survives the cut.
	HarvestOrgans []string
	// VitalParts are the body parts whose removal kills: a vital tag no
	// other part of the body provides.
	VitalParts map[string]bool
}

// HarvestOrgan reports whether the body part is a paired organ harvest takes.
func (f RecipeFacts) HarvestOrgan(part string) bool {
	return slices.Contains(f.HarvestOrgans, part)
}

// keptPart reports whether an added part on the body part stays on a
// prisoner: its removal kills (VitalParts) or is a keptBodyParts judgment.
func (f RecipeFacts) keptPart(body string) bool {
	return f.VitalParts[body] || keptBodyParts[body]
}

// BillWorkOf is the work type of a bill on the recipe, false when no bench
// the catalog knows hosts the recipe.
func (f RecipeFacts) BillWorkOf(recipe string) (WorkType, bool) {
	work, ok := f.BillWork[recipe]
	return work, ok
}

// MaterialInstall is a surgery recipe that installs a part made straight from
// a stuff: the recipe, the body parts it applies to, the hediff it adds, the
// recipe's work and the market value of the stuff it consumes.
type MaterialInstall struct {
	Recipe string
	Bodies []string
	Part   string
	Work   float64
	// Material is the stuff def the recipe consumes and Value its market value
	// times the count the recipe takes.
	Material string
	Value    float64
}

// MaterialInstallFor is the material install recipe that serves the body part,
// false when no stuff-made part fits it.
func (f RecipeFacts) MaterialInstallFor(body string) (MaterialInstall, bool) {
	for _, m := range f.MaterialInstalls {
		if slices.Contains(m.Bodies, body) {
			return m, true
		}
	}
	return MaterialInstall{}, false
}

// MaterialInstallByRecipe is the material install of the recipe.
func (f RecipeFacts) MaterialInstallByRecipe(recipe string) (MaterialInstall, bool) {
	for _, m := range f.MaterialInstalls {
		if m.Recipe == recipe {
			return m, true
		}
	}
	return MaterialInstall{}, false
}

// MaterialInstallOfPart is the material install that adds the hediff.
func (f RecipeFacts) MaterialInstallOfPart(hediff string) (MaterialInstall, bool) {
	for _, m := range f.MaterialInstalls {
		if m.Part == hediff {
			return m, true
		}
	}
	return MaterialInstall{}, false
}
