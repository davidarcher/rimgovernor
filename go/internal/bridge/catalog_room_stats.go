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
	for i := range stages {
		out.Stages = append(out.Stages, score(i))
	}
	for _, v := range out.Stages {
		if math.IsNaN(v) || math.IsInf(v, 0) {
			return policy.ImpressivenessLevels{}, contract("room stat %s has a non-finite score stage", RoomStatImpressiveness)
		}
	}
	if !(out.Dull > 0 && out.Dull < out.Mediocre && out.Mediocre < out.Decent && out.Decent < out.SlightlyImpressive) {
		return policy.ImpressivenessLevels{}, contract("room stat %s stage scores %v do not rise from a positive first stage", RoomStatImpressiveness, out)
	}
	return out, nil
}

// roomStatFoodPoisonChance is the RoomStatDef a cook's meals read; its input
// is the room's Cleanliness.
const roomStatFoodPoisonChance = "FoodPoisonChance"

// humanlikeBloodDef is the BloodDef most humanlike races name, the filth
// Corpse.ButcherProducts spawns once per butchered colonist-sized corpse
// (ties by name); empty when no humanlike race names one.
func (catalog *DefinitionCatalog) humanlikeBloodDef() string {
	count := map[string]int{}
	for _, row := range catalog.ThingDefs {
		if race := row.GetRace(); race.GetIntelligence() >= d.Intelligence_INTELLIGENCE_HUMANLIKE && race.GetBloodDef() != "" {
			count[race.GetBloodDef()]++
		}
	}
	best := ""
	for def, n := range count {
		if n > count[best] || n == count[best] && (best == "" || def < best) {
			best = def
		}
	}
	return best
}

// FilthModel is the price of a butchering to a room, from the defs: the
// humanlike blood filth's Cleanliness stat and the FoodPoisonChance curve. A
// catalog without either row prices nothing (the zero model).
func (catalog *DefinitionCatalog) FilthModel() policy.FilthModel {
	name := catalog.humanlikeBloodDef()
	blood := catalog.ThingDefs[name]
	stat := DefRow[*d.RoomStatDef](catalog, roomStatFoodPoisonChance)
	if blood == nil || stat == nil {
		return policy.FilthModel{}
	}
	out := policy.FilthModel{Def: name}
	for _, sm := range blood.GetStatBases() {
		if sm.GetValue().GetStat() == "Cleanliness" {
			out.Cleanliness = float64(sm.GetValue().GetValue())
		}
	}
	for _, p := range stat.GetCurve().GetPoints() {
		out.FoodPoison = append(out.FoodPoison, policy.CurvePoint{X: float64(p.GetLoc().GetX()), Y: float64(p.GetLoc().GetY())})
	}
	return out
}
