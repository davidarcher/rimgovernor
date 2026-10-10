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
		{"traitStatWeights", "RimWorld.StatDef", sortedKeys(traitStatWeights)},
		{"sociableTraits", "RimWorld.TraitDef", sortedKeys(sociableTraits)},
		{"traitThoughts", "RimWorld.ThoughtDef", slices.Clone(traitThoughts)},
		{"suitePressureThoughts", "RimWorld.ThoughtDef", sortedKeys(suitePressureThoughts)},
		{"GameRoomRoleDefs", "Verse.ThingDef", sortedKeys(GameRoomRoleDefs)},
		{"NonFleshMeatDefs", "Verse.ThingDef", slices.Clone(NonFleshMeatDefs)},
		{"QuestGiftDefs", "Verse.ThingDef", slices.Clone(QuestGiftDefs)},
	}
}

// QuestGiftDefs are the items a Beggars quest's request may be answered with.
// Native gives whatever the lord toil requests, so which items the colony
// hands over is policy's choice, not a game rule.
var QuestGiftDefs = []string{"Beer", "MedicineHerbal", "MedicineIndustrial", "Penoxycyline", "Silver"}

// GameRoomRoleDefs are the ThingDefOf names the game's room-role workers score a
// furniture role by: a def of the name has the role.
var GameRoomRoleDefs = map[string]FurnitureRole{
	"ToyBox": RoleToy, "BabyDecoration": RoleDecoration, "Blackboard": RoleBoard, "SchoolDesk": RoleDesk,
}

// NonFleshMeatDefs is the one meat def ThingDefGenerator_Meat gives a pawn def
// whose flesh type is not organic (ThingDefOf.Steel).
var NonFleshMeatDefs = []string{"Steel"}

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
