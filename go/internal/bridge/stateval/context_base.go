package stateval

// The facts the base StatWorker reads from a thing request (epic #2621,
// #2639): the thing's quality, comps, minified inner thing and biocoding, and
// for a pawn its skills, capacities, traits, hediffs, ideology, genes, life
// stage, worn gear and inspiration. Def-side data (trait degrees, hediff
// stages, precept, gene, life stage and inspiration offsets, skill needs) is
// read from the mirrored rows by name; a Known fact below is only what the
// rows cannot hold. A fact the evaluation reads and the caller did not state
// is an error naming it (need), never a default.

// StatMod is one live stat modifier a fact carries (StatModifier).
type StatMod struct {
	Stat  string
	Value float32
}

// QualityState is Thing.TryGetQuality: Has is false for a thing with no
// CompQuality (and its category is then Normal, as the game's StatRequest).
type QualityState struct {
	Has bool
	// Category is the QualityCategory, 0 awful to 6 legendary.
	Category int32
}

// FacilityLink is one entry of CompAffectedByFacilities.linkedFacilities: the
// linked facility's CompFacility.StatOffsets (nil when the game's list is
// null; CompFacilityQualityBased computes its own) and CanBeActive.
type FacilityLink struct {
	StatOffsets []StatMod
	Active      bool
}

// BaseFacts are the thing facts the base worker reads beyond StatContext's
// older fields. Fields not about comps are required by the term that reads
// them only.
type BaseFacts struct {
	// Quality is Thing.TryGetQuality of the thing.
	Quality Known[QualityState]
	// UniqueWeaponTraits are CompUniqueWeapon.TraitsListForReading (the
	// WeaponTraitDef names); read for a def with a CompUniqueWeapon comp, whose
	// GetStatOffset and GetStatFactor sum their statOffsets and statFactors.
	UniqueWeaponTraits Known[[]string]
	// Facilities are CompAffectedByFacilities.linkedFacilities; read for a def
	// with that comp, whose GetStatOffset sums the active facilities' offsets.
	Facilities Known[[]FacilityLink]
	// Biocoded is CompBiocodable.Biocoded; read for a def with that comp.
	Biocoded Known[bool]
	// Minified is MinifiedThing.InnerThing; read for a MinifiedThing when the
	// stat's minifiedThingInherits. Some(nil) is a null inner thing (the game
	// logs and evaluates the minified thing itself).
	Minified Known[*Subject]
	// SpawnedOrParentSpawned is Thing.SpawnedOrAnyParentSpawned and InSpaceLayer
	// MapHeld.Tile's layer isSpace (a valid tile in a space layer); read by
	// ConditionalStatAffecter_InSpace, the latter only when the former holds.
	SpawnedOrParentSpawned Known[bool]
	InSpaceLayer           Known[bool]
	// InSunlight is Position.InSunlight(Map); read by
	// ConditionalStatAffecter_InSunlight on a spawned thing.
	InSunlight Known[bool]
}

// SkillsState is pawn.skills: Present is false for a pawn without skills,
// Levels the SkillRecord.Level by SkillDef name (aptitudes and total
// disability included, as the game's property).
type SkillsState struct {
	Present bool
	Levels  map[string]int32
}

// TraitState is one Trait of pawn.story.traits.allTraits.
type TraitState struct {
	Def    string
	Degree int32
	// Suppressed is Trait.Suppressed.
	Suppressed bool
}

// StoryState is pawn.story: Present is false for a pawn without a story.
type StoryState struct {
	Present bool
	Traits  []TraitState
}

// Hediff stage kinds: a stage out of the def, no stage, or a stage the
// hediff class builds at runtime.
const (
	StageNone = iota
	StageDef
	StageSynthetic
)

// HediffStageState is Hediff.CurStage. Kind StageDef reads the def's stages
// at Index; StageSynthetic carries the runtime stage's stat offsets and
// factors (Hediff_BandNode's MechBandwidth offset; Hediff_DisruptorFlash's
// stage has none).
type HediffStageState struct {
	Kind        int
	Index       int32
	StatOffsets []StatMod
	StatFactors []StatMod
}

// GearPiece is a worn apparel or the primary weapon: a thing of its own,
// whose Subject (with its StatContext) runs the stat's parts in
// StatWorker.StatOffsetFromGear.
type GearPiece struct {
	Subject Subject
	// BladelinkTraits are CompBladelinkWeapon.TraitsListForReading; read for a
	// def with that comp.
	BladelinkTraits Known[[]string]
}

// WornGear is pawn.apparel: Present is false for a pawn without a tracker.
type WornGear struct {
	Present bool
	Worn    []GearPiece
}

// PawnBaseFacts are the pawn facts the base worker reads.
type PawnBaseFacts struct {
	Skills Known[SkillsState]
	// Capacities are pawn.health.capacities.GetLevel by PawnCapacityDef name.
	Capacities Known[map[string]float32]
	Story      Known[StoryState]
	// The hediffs, ideo, genes and life stage are BodyFacts' (Hediffs, Ideo,
	// Genes, CurLifeStage), shared with the pawn-group parts.
	Apparel Known[WornGear]
	// Primary is pawn.equipment.Primary; Some(nil) is no primary weapon (or
	// no equipment tracker).
	Primary Known[*GearPiece]
	// Inspiration is the InspirationDef name; Some("") is not inspired.
	Inspiration Known[string]
	// IsColonyMech is Pawn.IsColonyMech; KindDef the PawnKindDef name;
	// Developmental the DevelopmentalStage flags (1 newborn, 2 baby, 4 child,
	// 8 adult).
	IsColonyMech  Known[bool]
	KindDef       Known[string]
	Developmental Known[int32]
}
