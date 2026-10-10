package stateval

// PawnStatFacts are the pawn facts the pawnstat-group workers read
// (workers_pawnstat.go, epic #2621, #2639), stated by the caller of a thing
// request about a pawn. A fact the caller does not state is an error naming it.
type PawnStatFacts struct {
	// IsMechanitor is MechanitorUtility.IsMechanitor(pawn).
	IsMechanitor Known[bool]
	// TerrorIntensities are the intensity of each Thought_MemoryObservationTerror
	// in pawn.needs.mood.thoughts.memories, in list order (TerrorUtility.GetTerrorThoughts).
	TerrorIntensities Known[[]int32]
	// IsAnimal is Pawn.IsAnimal: RaceProps.Animal and not subhuman.
	IsAnimal Known[bool]
	// Suppression is the pawn's Need_Suppression (needs.TryGetNeed).
	Suppression Known[SuppressionNeedState]
	// MutantDef is the def name of pawn.mutant; Some("") is a pawn that is not a
	// mutant (Pawn.IsMutant false).
	MutantDef Known[string]
	// Training is pawn.training; Some(TrainingState{}) is a pawn with none.
	Training Known[TrainingState]
}

// SuppressionNeedState is the pawn's Need_Suppression, if it has one.
type SuppressionNeedState struct {
	Present bool
	// CurLevelPercentage is Need.CurLevelPercentage.
	CurLevelPercentage float32
}

// TrainingState is pawn.training.
type TrainingState struct {
	Present bool
	// ForageLearned is Pawn_TrainingTracker.HasLearned(TrainableDefOf.Forage),
	// trainability check included.
	ForageLearned bool
}
