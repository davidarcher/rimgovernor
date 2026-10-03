package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// RoomStatImpressiveness is the RoomStatDef the room Impressiveness stat reads
// and whose score stages name the impressiveness labels.
const RoomStatImpressiveness = "Impressiveness"

// impressivenessStages are the positions of the stages the planners aim at in
// the Impressiveness stat's score stages, which run awful, dull, mediocre,
// decent, slightly impressive and on up.
const (
	impressivenessDull = iota + 1
	impressivenessMediocre
	impressivenessDecent
	impressivenessSlightlyImpressive
)

// ImpressivenessLevels are the minimum scores of the stages the planners aim
// at, read from the Impressiveness RoomStatDef's score stages. A catalog
// without the row, with too few stages, or whose stage scores do not rise from
// a positive dull stage is an error.
func (catalog *DefinitionCatalog) ImpressivenessLevels() (policy.ImpressivenessLevels, error) {
	row := DefRow[*d.RoomStatDef](catalog, RoomStatImpressiveness)
	if row == nil {
		return policy.ImpressivenessLevels{}, contract("catalog has no %s room stat", RoomStatImpressiveness)
	}
	stages := row.GetScoreStages()
	if len(stages) <= impressivenessSlightlyImpressive {
		return policy.ImpressivenessLevels{}, contract("room stat %s has %d score stages, want more than %d", RoomStatImpressiveness, len(stages), impressivenessSlightlyImpressive)
	}
	score := func(i int) float64 { return float64(stages[i].GetValue().GetMinScore()) }
	out := policy.ImpressivenessLevels{
		Dull: score(impressivenessDull), Mediocre: score(impressivenessMediocre),
		Decent: score(impressivenessDecent), SlightlyImpressive: score(impressivenessSlightlyImpressive),
	}
	for _, v := range []float64{out.Dull, out.Mediocre, out.Decent, out.SlightlyImpressive} {
		if math.IsNaN(v) || math.IsInf(v, 0) {
			return policy.ImpressivenessLevels{}, contract("room stat %s has a non-finite score stage", RoomStatImpressiveness)
		}
	}
	if !(out.Dull > 0 && out.Dull < out.Mediocre && out.Mediocre < out.Decent && out.Decent < out.SlightlyImpressive) {
		return policy.ImpressivenessLevels{}, contract("room stat %s stage scores %v do not rise from a positive first stage", RoomStatImpressiveness, out)
	}
	return out, nil
}
