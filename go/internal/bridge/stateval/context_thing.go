package stateval

// ThingFacts are the thing and map facts the thing-group parts and workers
// read beyond StatContext (parts_thing.go, workers_thing.go). Every fact is
// Known: a thing request that does not state one a class reads gets an error
// naming it. What the thing's def row already holds (its class, comps,
// building and plant properties, category) is read off the row, not stated.
type ThingFacts struct {
	// RainRate is map.weatherManager.RainRate.
	RainRate Known[float32]
	// RoomUsesOutdoorTemperature is Thing.GetRoom() != null &&
	// room.UsesOutdoorTemperature.
	RoomUsesOutdoorTemperature Known[bool]
	// Terrain is the TerrainDef name at the thing's cell; Some("") is a cell
	// with no terrain def.
	Terrain Known[string]
	// ProtectedByEdifice is SteadyEnvironmentEffects.ProtectedByEdifice at the
	// thing's cell.
	ProtectedByEdifice Known[bool]
	// Polluted is pollutionGrid.IsPolluted at the thing's cell.
	Polluted Known[bool]

	// BeautyOffset is IBeautyContainer.BeautyOffset; Some(nil) is a thing that
	// is not an IBeautyContainer.
	BeautyOffset Known[*float32]
	// CorpseCasketHasCorpse is Building_CorpseCasket.HasCorpse; read only
	// when the def's thingClass is a Building_CorpseCasket.
	CorpseCasketHasCorpse Known[bool]
	// HarbingerBeingConsumed is CompHarbingerTreeConsumable.BeingConsumed,
	// false for a thing without the comp.
	HarbingerBeingConsumed Known[bool]
	// RelicContained is CompRelicContainer.ContainedThing != null, false for a
	// thing without the comp.
	RelicContained Known[bool]
	// Biocoded is CompBiocodable.IsBiocoded, false for a thing without the comp.
	Biocoded Known[bool]
	// PowerTraderOff is a CompPowerTrader that exists and is not PowerOn;
	// false for a thing without the comp.
	PowerTraderOff Known[bool]

	// HitPoints and MaxHitPoints are Thing.HitPoints and Thing.MaxHitPoints.
	HitPoints    Known[int32]
	MaxHitPoints Known[int32]
	// Rot is Thing.GetRotStage(): ThingRotFresh, ThingRotRotting or ThingRotDessicated
	// (ThingRotFresh for a thing without CompRottable).
	Rot Known[string]
	// PlantGrowth is Plant.Growth, read only for a Plant.
	PlantGrowth Known[float32]
	// UnfinishedIngredients are UnfinishedThing.ingredients in list order,
	// read only for an UnfinishedThing.
	UnfinishedIngredients Known[[]IngredientMass]
	// ArtificialBuildingsNear is the count of listerArtificialBuildingsForMeditation
	// .GetForCell(position, radius) by radius.
	ArtificialBuildingsNear Known[map[float32]int32]

	// NoxiousHaze, ToxicFallout are the map-condition facts of those parts.
	NoxiousHaze  NoxiousHazeFacts
	ToxicFallout ToxicFalloutFacts

	// ShowTrapDamageStat is Building_Trap.ShouldShowTrapDamageStat, read only
	// for a Building_Trap.
	ShowTrapDamageStat Known[bool]
	// PawnIsMutant is Pawn.IsMutant, read only for a pawn with CompStudiable.
	PawnIsMutant Known[bool]
	// AmbientTemperature is Thing.AmbientTemperature.
	AmbientTemperature Known[float32]
	// Containment is StatWorker_ContainmentStrength's room inputs; Some(nil)
	// is a thing with no room, no map, or a room that touches the map edge.
	Containment Known[*ContainmentRoom]
	// ResearchFinished are the ResearchProjectDef names whose IsFinished is
	// true; read for power upgrades only.
	ResearchFinished Known[map[string]bool]
}

// The RotStage members.
const (
	ThingRotFresh      = "Fresh"
	ThingRotRotting    = "Rotting"
	ThingRotDessicated = "Dessicated"
)

// IngredientMass is one UnfinishedThing ingredient: the stack count and the
// ingredient's own GetStatValue(Mass).
type IngredientMass struct {
	Mass       float32
	StackCount int32
}

// NoxiousHazeFacts are the inputs of NoxiousHazeUtility.IsExposedToNoxiousHaze,
// read in the game's order, each only when the ones before it allow.
type NoxiousHazeFacts struct {
	// SpawnedOrAnyParentSpawned is Thing.SpawnedOrAnyParentSpawned.
	SpawnedOrAnyParentSpawned Known[bool]
	// Active is the map's NoxiousHaze condition being active and not
	// HiddenByOtherCondition, at the thing's MapHeld.
	Active Known[bool]
	// PawnImmune is Pawn.kindDef.immuneToGameConditionEffects, read for a pawn.
	PawnImmune Known[bool]
	// HeldRoofed is PositionHeld.Roofed(MapHeld).
	HeldRoofed Known[bool]
	// HeldRoomPsychologicallyOutdoors is PositionHeld.GetRoom(MapHeld)
	// .PsychologicallyOutdoors, read for a roofed non-item.
	HeldRoomPsychologicallyOutdoors Known[bool]
}

// ToxicFalloutFacts are the inputs of StatPart_ToxicFallout.ActiveFor beyond
// the def, read in order.
type ToxicFalloutFacts struct {
	// HasMapHeld is Thing.MapHeld != null.
	HasMapHeld Known[bool]
	// Active is MapHeld.gameConditionManager.ConditionIsActive(ToxicFallout).
	Active Known[bool]
	// PositionHeldValid is PositionHeld.IsValid.
	PositionHeldValid Known[bool]
	// PositionHeldRoofed is PositionHeld.Roofed(MapHeld).
	PositionHeldRoofed Known[bool]
}

// ContainmentRoom is the room of a thing that StatWorker_ContainmentStrength
// scores.
type ContainmentRoom struct {
	PsychologicallyOutdoors bool
	// CellGlows is glowGrid.GroundGlowAt of each room cell, in room.Cells order.
	CellGlows []float32
	// CellCount is Room.CellCount.
	CellCount int32
	// OpenRoofCount is Room.OpenRoofCount.
	OpenRoofCount int32
	// Doors are the room's portal doors (the distinct ones the game collects).
	Doors []ContainmentDoor
	// OtherHolders counts the EntityHolder things in ContainedAndAdjacentThings
	// other than the thing itself.
	OtherHolders int32
	// OpenCellTerrains are the TerrainDef names of the room cells that are in
	// bounds and not filled, in room.Cells order.
	OpenCellTerrains []string
	// BorderEdificeHitPoints are the HitPoints of the edifice on each distinct
	// in-bounds border cell that has one and is not a door.
	BorderEdificeHitPoints []int32
}

// ContainmentDoor is one Building_Door of the room.
type ContainmentDoor struct {
	HitPoints         int32
	ContainmentBreach bool
}
