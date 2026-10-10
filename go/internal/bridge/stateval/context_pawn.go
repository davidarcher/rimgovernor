package stateval

// BodyFacts are the pawn facts the pawn-group parts and workers read beyond
// PawnState (parts_pawn.go, workers_pawn.go). For a Corpse thing the same
// struct describes the inner pawn (StatContext.Corpse). A fact the caller does
// not state is an error naming it, never a default.
type BodyFacts struct {
	// CurLifeStage is ageTracker.CurLifeStage's LifeStageDef name; its
	// bodySizeFactor and foodMaxFactor are read from the def row.
	CurLifeStage Known[string]
	// NaturalCoverage is hediffSet.GetCoverageOfNotMissingNaturalParts(
	// body.corePart).
	NaturalCoverage Known[float32]
	// Hediffs is health.hediffSet.hediffs in list order. Each hediff's class
	// is its def's hediffClass.
	Hediffs Known[[]HediffState]
	// Genes is pawn.genes; Some(GenesState{}) is a pawn without a gene tracker.
	Genes Known[GenesState]
	// Subhuman is Pawn.IsSubhuman.
	Subhuman Known[bool]
	// DigLearned is pawn.training?.HasLearned(TrainableDefOf.Dig) ?? false.
	DigLearned Known[bool]
	// Terror is pawn.GetStatValue(StatDefOf.Terror).
	Terror Known[float32]
	// Crawling is Pawn.Crawling.
	Crawling Known[bool]
	// Revenant holds the facts StatPart_RevenantSpeed reads.
	Revenant Known[RevenantState]
	// SightSourcesAllMissing is whether every body part tagged SightSource is
	// missing (true for a body with none): StatPart_BlindPsychicSensitivityOffset.ConsideredBlind.
	SightSourcesAllMissing Known[bool]
	// SightEfficiency is PawnCapacityUtility.CalculateTagEfficiency(hediffSet,
	// SightSource, float.MaxValue, default, null,
	// PawnCapacityWorker_Sight.PartEfficiencySpecialWeight).
	SightEfficiency Known[float32]
	// Ideo is pawn.Ideo; Some(IdeoState{}) is a pawn without one.
	Ideo Known[IdeoState]
	// Overseer is pawn.GetOverseer(); Some(OverseerState{}) is a pawn without.
	Overseer Known[OverseerState]
	// PlayerFactionLeader is faction.IsPlayer && faction.leader == thing.
	PlayerFactionLeader Known[bool]
	// Bed is the bed StatPart_BedStat reads.
	Bed Known[BedState]
	// TicksGame is Find.TickManager.TicksGame.
	TicksGame Known[int32]
}

// HediffState is one hediff of pawn.health.hediffSet.hediffs.
type HediffState struct {
	// Def is the HediffDef name.
	Def string
	// Permanent is Hediff.IsPermanent().
	Permanent bool
	// Severity and Stage are what the base worker reads (Hediff.Severity,
	// Hediff.CurStage).
	Severity float32
	Stage    HediffStageState
}

// GenesState is the pawn's gene tracker; the base worker reads the genes
// that are not overridden (Gene.Active).
type GenesState struct {
	// Present is pawn.genes != null.
	Present bool
	// Genes is GenesListForReading in order.
	Genes []GeneState
}

// GeneState is one Gene.
type GeneState struct {
	// Def is the GeneDef name; its biostatMet is read from the def row.
	Def        string
	Overridden bool
}

// RevenantState is the mind state and kind of a pawn that StatPart_RevenantSpeed
// reads.
type RevenantState struct {
	// KindDef is pawn.kindDef's PawnKindDef name.
	KindDef string
	// PsychologicallyInvisible is Pawn.IsPsychologicallyInvisible().
	PsychologicallyInvisible bool
	// LastBecameVisibleTick and LastForcedVisibleTick are the mindState fields.
	LastBecameVisibleTick, LastForcedVisibleTick int32
}

// IdeoState is a pawn's ideo.
type IdeoState struct {
	// Present is pawn.Ideo != null.
	Present bool
	// Precepts are the PreceptDef names of PreceptsListForReading in order
	// (repeated when the ideo holds several precepts of one def).
	Precepts []string
	// Role is the PreceptDef name of Ideo.GetRole(pawn), or "" for no role.
	Role string
}

// OverseerState is a pawn's overseer.
type OverseerState struct {
	// Present is GetOverseer() != null.
	Present bool
	// Stats are overseer.GetStatValue by StatDef name; a stat not listed is
	// unknown.
	Stats map[string]float32
}

// Bed kinds for BedState.
const (
	BedNone    = "None"
	BedBuilt   = "Built"
	BedCaravan = "CaravanBed"
)

// BedState is the pawn's bed: pawn.InBed() then pawn.InCaravanBed() decide the
// kind, and the bed thing's stat values are read by StatDef name.
type BedState struct {
	// Kind is BedNone, BedBuilt (pawn.CurrentBed()) or BedCaravan
	// (pawn.CurrentCaravanBed()).
	Kind  string
	Stats map[string]float32
}
