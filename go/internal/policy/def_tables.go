package policy

import (
	"slices"
	"sort"
)

// DefTable is one judgment table of game def names that policy keeps because
// the game offers no signal to derive it from. Class is the CLR full name of
// the Def class every name must be a row of (a subclass row counts); the
// dangling-reference gate in bridge checks each name against the recorded
// catalog, so a typo or a renamed def fails a test rather than silently never
// matching.
type DefTable struct {
	Table string
	Class string
	Names []string
}

// DefTables lists every judgment table of def names. A table that holds def
// names registers here; a table derived from rows does not.
func DefTables() []DefTable {
	return []DefTable{
		{"unrecoveredParts", "Verse.HediffDef", sortedKeys(unrecoveredParts)},
		{"keptBodyParts", "Verse.BodyPartDef", sortedKeys(keptBodyParts)},
		{"herdTrainables", "RimWorld.TrainableDef", sortedKeys(herdTrainables)},
		{"threatKinds", "Verse.PawnKindDef", sortedKeys(threatKinds)},
		{"threatWeapons", "Verse.ThingDef", sortedKeys(threatWeapons)},
		{"ArmorResearchRungs", "Verse.ResearchProjectDef", slices.Clone(ArmorResearchRungs)},
		{"gearValuables", "Verse.ThingDef", resourceKeys(gearValuables)},
	}
}

func resourceKeys(set map[Resource]bool) []string {
	out := make([]string, 0, len(set))
	for name := range set {
		out = append(out, string(name))
	}
	sort.Strings(out)
	return out
}

func sortedKeys[V any](set map[string]V) []string {
	out := make([]string, 0, len(set))
	for name := range set {
		out = append(out, name)
	}
	sort.Strings(out)
	return out
}
