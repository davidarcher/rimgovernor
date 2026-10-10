package bridge

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// traitDegreeData is the degree's data of a trait row; nil when the trait has
// no such degree.
func traitDegreeData(row *d.TraitDef, degree int) *d.TraitDegreeData {
	for _, entry := range row.GetDegreeDatas() {
		if int(entry.GetValue().GetDegree()) == degree {
			return entry.GetValue()
		}
	}
	return nil
}

// workerAlwaysActive is the ThoughtDef worker of a thought that holds for as
// long as the pawn has the trait (the NaturalMood offsets): its first stage
// is a permanent mood offset. Any other worker's thought is situational.
const workerAlwaysActive = "RimWorld.ThoughtWorker_AlwaysActive"

// noRequiredDegree is requiredTraitsDegree of a thought that requires its
// trait at any degree (the field's unset value, int.MinValue).
const noRequiredDegree = math.MinInt32

// TraitBalance is what a trait degree does to the terms a prospect is valued
// by (policy.TraitBalance.Worth weighs them): the stat offsets and the
// distance of each stat factor from 1, the hunger-rate factor above 1, the
// work types it disables, its code-applied sociability, and its mood: the
// first stage of each always-active thought that requires the trait at this
// degree, and the worst stage of each situational one when that is negative.
// A trait or degree the catalog lacks is a contract error.
func (catalog *DefinitionCatalog) TraitBalance(name string, degree int) (policy.TraitBalance, error) {
	row := DefRow[*d.TraitDef](catalog, name)
	if row == nil {
		return policy.TraitBalance{}, contract("catalog has no trait %s", name)
	}
	data := traitDegreeData(row, degree)
	if data == nil {
		return policy.TraitBalance{}, contract("catalog trait %s has no degree %d", name, degree)
	}
	effects, err := catalog.TraitEffects(name, degree)
	if err != nil {
		return policy.TraitBalance{}, err
	}
	out := policy.TraitBalance{Stats: map[string]float64{}, DisabledWork: len(effects.DisabledWork), Sociable: effects.Sociable}
	for _, entry := range data.GetStatOffsets() {
		out.Stats[entry.GetValue().GetStat()] += float32Number(entry.GetValue().GetValue())
	}
	for _, entry := range data.GetStatFactors() {
		out.Stats[entry.GetValue().GetStat()] += float32Number(entry.GetValue().GetValue()) - 1
	}
	if rate := data.GetHungerRateFactor(); rate > 1 {
		out.HungerRate = float32Number(rate) - 1
	}
	for _, def := range catalogDefs[*d.ThoughtDef](catalog) {
		thought := def.(*d.ThoughtDef)
		required := thought.GetRequiredTraitsDegree()
		if !slices.Contains(thought.GetRequiredTraits(), name) || required != noRequiredDegree && int(required) != degree {
			continue
		}
		stages := thought.GetStages()
		if len(stages) == 0 {
			continue
		}
		if thought.GetWorkerClass() == workerAlwaysActive {
			out.Mood += float32Number(stages[0].GetValue().GetBaseMoodEffect())
			continue
		}
		worst := 0.0
		for _, stage := range stages {
			worst = min(worst, float32Number(stage.GetValue().GetBaseMoodEffect()))
		}
		out.Mood += worst
	}
	return out, nil
}
