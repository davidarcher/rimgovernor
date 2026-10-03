package policy

import (
	"slices"
)

// RecipeFacts are what the catalog's RecipeDef rows say about the recipes the
// planners treat specially (#1721). Names are the recipes' defNames; every
// fact is derived from the rows, never from a name.
type RecipeFacts struct {
	// MaterialInstalls are the install recipes of parts made straight from a
	// stuff (a peg leg from a log), by recipe.
	MaterialInstalls []MaterialInstall
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
