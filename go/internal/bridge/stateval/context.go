package stateval

import "fmt"

// The state a thing request reads that the rows do not hold (epic #2621,
// #2638). A definition request (Subject.Context nil) has no thing, so every
// state-reading StatPart sees StatRequest.HasThing false and leaves the value
// alone (StatPart_Outdoors applies its indoors factor). A thing request
// carries a StatContext holding exactly the facts the ported parts read; a
// fact the caller does not state is an error naming it, never a default.

// Known is one stated fact: Some(v), or the zero value, which is "not
// observed".
type Known[T any] struct {
	V  T
	OK bool
}

// Some is a stated fact.
func Some[T any](v T) Known[T] { return Known[T]{V: v, OK: true} }

// need is the fact's value, or the error naming the missing input.
func need[T any](k Known[T], input string) (T, error) {
	if !k.OK {
		var zero T
		return zero, fmt.Errorf("stat evaluation needs %s: not observed", input)
	}
	return k.V, nil
}

// StatContext is the live state of the thing a request is about.
type StatContext struct {
	// Pawn is the pawn's state; nil means the thing is not a Pawn.
	Pawn *PawnState
	// Spawned is Thing.Spawned.
	Spawned Known[bool]
	// Room is Thing.GetRoom(): Some(nil) is a thing in no room.
	Room Known[*RoomState]
	// Roofed is the roof grid at the thing's cell.
	Roofed Known[bool]
	// GlowLevel is glowGrid.GroundGlowAt of the thing's cell.
	GlowLevel Known[float32]
	// Temperature is GenTemperature.GetTemperatureForCell of the thing's cell.
	Temperature Known[float32]
	// Genepack is the thing's GeneSet when it is a Genepack with one; Some(nil)
	// is any other thing.
	Genepack Known[*GeneSetState]
}

// RoomState is the Room facts the parts read.
type RoomState struct {
	OutdoorsForWork         Known[bool]
	PsychologicallyOutdoors Known[bool]
	// Role is the RoomRoleDef name.
	Role Known[string]
	// Stats are Room.GetStat by RoomStatDef name; a stat not listed is unknown.
	Stats map[string]float32
}

// GeneSetState is the GeneSet facts StatPart_Genes reads.
type GeneSetState struct {
	// MarketValueFactors are the genes' marketValueFactor in list order.
	MarketValueFactors []float32
	ArchitesTotal      int32
}

// PawnState is the Pawn facts the parts read. The race facts (humanlike, life
// expectancy) are the def row's, not repeated here.
type PawnState struct {
	// Age is the age tracker; Some(AgeState{}) is a pawn with none.
	Age Known[AgeState]
	// Female is Pawn.gender == Female.
	Female Known[bool]
	// Rest and Food are the needs; Some(NeedState{}) is a pawn without the need.
	Rest Known[NeedState]
	Food Known[NeedState]
	// Mood is needs.mood.CurLevel; Some(MoodState{}) is a pawn without it.
	Mood Known[MoodState]
	// PainTotal is health.hediffSet.PainTotal.
	PainTotal Known[float32]
	// Malnutrition is the first Malnutrition hediff.
	Malnutrition Known[MalnutritionState]
	// FertilityFactors are the fertilityFactor of each hediff's current stage,
	// in hediff order, for hediffs that have a stage.
	FertilityFactors Known[[]float32]
	Resting          Known[RestingState]
	IsSlave          Known[bool]
	IsWildMan        Known[bool]
	Deathresting     Known[bool]
	// Glow inputs: Shambler is Pawn.IsShambler; Blind is
	// PawnUtility.IsBiologicallyOrArtificiallyBlind; IdeoPrefersDarkness is
	// Ideo != null && IdeoPrefersDarkness(); GenesAffectedByDarkness is
	// genes == null || genes.AffectedByDarkness.
	Shambler                Known[bool]
	Blind                   Known[bool]
	IdeoPrefersDarkness     Known[bool]
	GenesAffectedByDarkness Known[bool]
}

const ticksPerYear = 3600000

// AgeState is Pawn_AgeTracker: the biological age in ticks, from which both
// AgeBiologicalYears (an int) and AgeBiologicalYearsFloat derive.
type AgeState struct {
	Tracked         bool
	BiologicalTicks int64
}

func (a AgeState) years() int32        { return int32(a.BiologicalTicks / ticksPerYear) }
func (a AgeState) yearsFloat() float32 { return float32(float32(a.BiologicalTicks) / 3600000) }

// Need categories, by the game's enum member name.
const (
	RestRested         = "Rested"
	RestTired          = "Tired"
	RestVeryTired      = "VeryTired"
	RestExhausted      = "Exhausted"
	FoodFed            = "Fed"
	FoodHungry         = "Hungry"
	FoodUrgentlyHungry = "UrgentlyHungry"
	FoodStarving       = "Starving"
)

// NeedState is a need's CurCategory.
type NeedState struct {
	Present  bool
	Category string
}

// MoodState is needs.mood.CurLevel.
type MoodState struct {
	Present bool
	Level   float32
}

// MalnutritionState is the pawn's Malnutrition hediff, if any.
type MalnutritionState struct {
	Present  bool
	Severity float32
}

// RestingState is the inputs of StatPart_Resting's predicate.
type RestingState struct {
	InBed, NotStanding, Downed     bool
	CaravanMember, CaravanMoving   bool
	InCaravanBed, CarriedByCaravan bool
}

// pawn is the request's pawn state: nil for a definition request or a
// non-pawn thing, where `req.Thing is Pawn` is false.
func (r *Request) pawn() *PawnState {
	if c := r.Subject.Context; c != nil {
		return c.Pawn
	}
	return nil
}
