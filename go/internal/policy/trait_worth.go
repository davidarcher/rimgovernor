package policy

// Trait worth: whether a trait degree is good or bad in a prospect is the sign
// of its net effect on the terms the planners value, computed from the
// degree's TraitDef row (DefinitionCatalog.TraitBalance) and weighted here, in
// one place. The weights are the judgment; every effect they multiply is read.

// traitStatWeights is the worth of one unit of change in a StatDef by a trait
// degree: a stat offset counts as it is, a stat factor as its distance from 1.
// The weight is signed: MentalBreakThreshold and incoming damage are better
// lower, so their weights are negative. Registered in DefTables.
var traitStatWeights = map[string]float64{
	"WorkSpeedGlobal":      1,
	"GlobalLearningFactor": 0.25,
	"MentalBreakThreshold": -1,
	"PainShockThreshold":   1,
	"IncomingDamageFactor": -0.5,
	"ImmunityGainSpeed":    0.5,
	"MoveSpeed":            0.25,
}

// The other terms' weights.
const (
	// traitMoodWeight is the worth of one mood point: a permanent offset
	// counts in full, a situational thought only at its worst stage when that
	// stage is negative (a risk, never a credit).
	traitMoodWeight = 0.05
	// traitHungerWeight is the worth of one unit of hunger-rate factor above
	// 1 (a trait that eats more costs the colony).
	traitHungerWeight = -0.2
	// traitDisabledWorkWeight is the worth of each work type a trait disables.
	traitDisabledWorkWeight = -0.1
	// traitSociableWeight is the worth of one step of sociability.
	traitSociableWeight = 0.1
)

// traitWorthEpsilon is the net below which a degree is neutral.
const traitWorthEpsilon = 1e-9

// TraitBalance is what a trait degree does to the terms a prospect is valued
// by, as the rows state them.
type TraitBalance struct {
	// Stats is the change per StatDef: the offset, or the factor minus 1.
	Stats map[string]float64
	// HungerRate is the hunger-rate factor minus 1.
	HungerRate float64
	// Mood is the mood points the trait carries (see traitMoodWeight).
	Mood float64
	// DisabledWork is the count of work types the trait disables.
	DisabledWork int
	// Sociable is the code-applied sociability (TraitSociable).
	Sociable int
}

// Worth is the weighted net of the balance: positive is a good trait,
// negative a bad one, zero (within epsilon) neutral.
func (b TraitBalance) Worth() float64 {
	worth := b.HungerRate*traitHungerWeight + b.Mood*traitMoodWeight +
		float64(b.DisabledWork)*traitDisabledWorkWeight + float64(b.Sociable)*traitSociableWeight
	for stat, delta := range b.Stats {
		worth += delta * traitStatWeights[stat]
	}
	if worth > -traitWorthEpsilon && worth < traitWorthEpsilon {
		return 0
	}
	return worth
}

// sociableTraits is the sociability the game applies to a trait in code, where
// no row of the TraitDef or a ThoughtDef states it: Kind +1, Abrasive -1
// (Kind and Abrasive only nullify the looks thoughts and change certainty
// loss in their rows). Registered in DefTables.
var sociableTraits = map[string]int{
	"Kind":     1,
	"Abrasive": -1,
}

// TraitSociable is the code-applied sociability of a trait; zero for a trait
// it does not list.
func TraitSociable(name string) int {
	return sociableTraits[name]
}

// The vanilla thoughts the game raises in code, whose rows state which
// traits take no mood from the act or carry a mood of their own: a trait
// that nullifies the thought of a prisoner dying (an execution), of a
// butchered human, or of being naked is Execution, HumanButcher or Nudist;
// the trait the carrying-a-ranged-weapon thought requires is MeleeOnly.
const (
	ThoughtPrisonerDied   = "KnowPrisonerDiedInnocent"
	ThoughtButcheredHuman = "ButcheredHumanlikeCorpse"
	ThoughtNaked          = "Naked"
	ThoughtRangedCarried  = "BrawlerUnhappy"
	// ThoughtOrganHarvested is the thought of a harvest from a colonist: a
	// trait that nullifies it is a surgeon who harvests without a mood loss.
	ThoughtOrganHarvested = "KnowColonistOrganHarvested"
	// ThoughtTaintedApparel is the thought of wearing a dead man's apparel.
	ThoughtTaintedApparel = "DeadMansApparel"
	// The thoughts whose worker applies to one trait only (requiredTraits):
	// a bedroom that is impressive or not (Greedy, Ascetic), a better bedroom
	// than the pawn's own (Jealous), the night and carrying an incendiary
	// weapon (NightOwl, Pyromaniac).
	ThoughtGreedy     = "Greedy"
	ThoughtAscetic    = "Ascetic"
	ThoughtJealous    = "Jealous"
	ThoughtNightOwl   = "NightOwlDuringTheNight"
	ThoughtPyromaniac = "PyromaniacHappy"
	// ThoughtDrugDesire is the thought a drug desire trait raises while
	// unsatisfied; its required trait is the DrugDesire trait, whose degree
	// is the chemical interest.
	ThoughtDrugDesire = "DrugDesireInterest"
)

// traitThoughts lists the thoughts TraitEffects reads by name. Registered in
// DefTables.
var traitThoughts = []string{
	ThoughtPrisonerDied, ThoughtButcheredHuman, ThoughtNaked, ThoughtRangedCarried,
	ThoughtOrganHarvested, ThoughtTaintedApparel, ThoughtGreedy, ThoughtAscetic,
	ThoughtJealous, ThoughtNightOwl, ThoughtPyromaniac, ThoughtDrugDesire,
}
