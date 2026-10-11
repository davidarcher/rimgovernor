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
		{"roomRoleScores", "Verse.RoomRoleDef", roomRoleScoreRoles()},
		{"RoomRoleNamedDefs", "Verse.ThingDef", sortedKeys(RoomRoleNamedDefs)},
		{"roleEffects", "Verse.RoomRoleDef", roleEffectRoles()},
		{"NonFleshMeatDefs", "Verse.ThingDef", slices.Clone(NonFleshMeatDefs)},
		{"MortarProbeShells", "Verse.ThingDef", slices.Clone(MortarProbeShells)},
		{"QuestGiftDefs", "Verse.ThingDef", slices.Clone(QuestGiftDefs)},
		{"ShelterHeatDef", "Verse.ThingDef", []string{ShelterHeatDef}},
		{"ShelterCoolDef", "Verse.ThingDef", []string{ShelterCoolDef}},
		{"WoodLatchDefs", "Verse.ThingDef", slices.Clone(WoodLatchDefs)},
		{"ScenarioThingChoices", "Verse.ThingDef", slices.Clone(ScenarioThingChoices)},
		{"RhadamantThings", "Verse.ThingDef", rhadamantThingDefs()},
	}
}

// ScenarioThingChoices are the Core items the launcher's scenario composer
// suggests for a starting thing. The composer takes any defName; this list only
// saves typing for common supplies.
var ScenarioThingChoices = []string{
	"Silver", "Gold", "WoodLog", "Steel", "Plasteel", "Uranium", "ComponentIndustrial", "ComponentSpacer",
	"MealSurvivalPack", "Pemmican", "MedicineHerbal", "MedicineIndustrial", "Gun_BoltActionRifle",
	"ShipChunk",
}

// RhadamantThings are the items of the launcher's Rhadamant Sea Ice preset.
var RhadamantThings = struct {
	Silver, Meal, Wood, Steel, Component, Medicine, Rifle, ShipChunk string
}{"Silver", "MealSurvivalPack", "WoodLog", "Steel", "ComponentIndustrial", "MedicineIndustrial", "Gun_BoltActionRifle", "ShipChunk"}

func rhadamantThingDefs() []string {
	t := RhadamantThings
	return []string{t.Silver, t.Meal, t.Wood, t.Steel, t.Component, t.Medicine, t.Rifle, t.ShipChunk}
}

// WoodLatchDefs are the stock defs the wood latch (WoodMin, WoodTarget) floors:
// the colony stage waives the latch while every one has no source.
var WoodLatchDefs = []string{"WoodLog"}

// MortarProbeShells is the one shell def BuildingProperties.IsMortar tests a
// loadable turret gun's fixed storage filter against (ThingDefOf.Shell_HighExplosive).
var MortarProbeShells = []string{"Shell_HighExplosive"}

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
