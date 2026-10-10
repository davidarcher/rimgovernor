package bridge

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The race rules the game's code states over def fields, derived from the
// mirrored rows (RaceProperties, FleshTypeDef, LifeStageDef, TrainableDef,
// TrainabilityDef) and the stat table. What the game's code computes beyond
// its fields stays native in RaceFacts: tameness decay, the wildness curve,
// the adult feed and the edible foods.

// The FleshTypeDefOf names RaceProperties compares with.
const (
	fleshNormal    = "Normal"
	fleshMechanoid = "Mechanoid"
	fleshInsectoid = "Insectoid"
)

// StatMeatAmount is the StatDef a butchery's meat yield reads.
const StatMeatAmount = "MeatAmount"

// anomalyFleshTypes are the flesh types RaceProperties.IsAnomalyEntity names;
// the defs exist only with Anomaly, so no race of another game refers to them.
var anomalyFleshTypes = []string{"EntityMechanical", "EntityFlesh", "Fleshbeast"}

// raceFleshType is RaceProperties.FleshType's name: a race with none is Normal.
func raceFleshType(props *d.RaceProperties) string {
	if flesh := props.GetFleshType(); flesh != "" {
		return flesh
	}
	return fleshNormal
}

// RaceFlags are the mechanoid and insect flags of def's race
// (RaceProperties.IsMechanoid and .Insect); false for a def without
// RaceProperties.
func (catalog *DefinitionCatalog) RaceFlags(def string) (mechanoid, insect bool) {
	props := catalog.ThingDef(def).GetRace()
	if props == nil {
		return false, false
	}
	flesh := raceFleshType(props)
	return flesh == fleshMechanoid, flesh == fleshInsectoid
}

// raceIsAnimal is RaceProperties.Animal: no tool use, organic flesh, and no
// Anomaly entity.
func (catalog *DefinitionCatalog) raceIsAnimal(def string, props *d.RaceProperties) (bool, error) {
	if props.GetIntelligence() >= d.Intelligence_INTELLIGENCE_TOOL_USER {
		return false, nil
	}
	flesh := raceFleshType(props)
	row := DefRow[*d.FleshTypeDef](catalog, flesh)
	if row == nil {
		return false, contract("race %s has flesh type %s with no row", def, flesh)
	}
	return row.GetIsOrganic() && !slices.Contains(anomalyFleshTypes, flesh), nil
}

// raceMeatDef is RaceProperties.meatDef, an unsaved field the game resolves
// from the rows: specificMeatDef, else the meat of useMeatFrom, else the meat
// ThingDefGenerator_Meat makes for a pawn def with meat and a corpse
// (Meat_<defName>; Steel for a race that is no flesh). Empty for a race with no
// meat.
func (catalog *DefinitionCatalog) raceMeatDef(row *d.ThingDef) string {
	props := row.GetRace()
	if meat := props.GetSpecificMeatDef(); meat != "" {
		return meat
	}
	if from := props.GetUseMeatFrom(); from != "" {
		return catalog.raceMeatDef(catalog.ThingDef(from))
	}
	if row.GetCategory() != d.ThingCategory_THING_CATEGORY_PAWN || !props.GetHasMeat() || !props.GetHasCorpse() {
		return ""
	}
	if flesh := DefRow[*d.FleshTypeDef](catalog, raceFleshType(props)); flesh != nil && !flesh.GetIsOrganic() {
		return policy.NonFleshMeatDefs[0]
	}
	return "Meat_" + row.GetDefName()
}

// RaceMeatNutrition is the Nutrition stat of the meat race's butchery yields,
// for any pawn def (the animal race table holds animals only); false for a race
// with no meat or a meat that shows no nutrition.
func (catalog *DefinitionCatalog) RaceMeatNutrition(race string) (float64, bool, error) {
	row := catalog.ThingDef(race)
	if row == nil || row.GetRace() == nil {
		return 0, false, nil
	}
	meat := catalog.raceMeatDef(row)
	if meat == "" {
		return 0, false, nil
	}
	n, shown, err := catalog.ShownStatValue(meat, "", StatNutrition)
	if err != nil || !shown || n <= 0 {
		return 0, false, err
	}
	return float64(n), true, nil
}

// raceTicks is a LifeStageAge's minAge in ticks: Mathf.FloorToInt(minAge *
// GenDate.TicksPerYear) in the game's single-precision arithmetic.
func raceTicks(minAge float32) int64 {
	return int64(math.Floor(float64(minAge * domain.TicksPerYear)))
}

// raceStageAges are the husbandry ages of a race in ticks: Adult is
// Pawn_AgeTracker.AdultMinAge (a humanlike race's first adult stage, otherwise
// the last stage), the others the minAge of the first stage (LifeStageAge order)
// whose LifeStageDef is reproductive, milkable or shearable, nil when none is.
type raceStageAges struct {
	Adult                             int64
	Reproductive, Milkable, Shearable *int64
}

func (catalog *DefinitionCatalog) raceStageAges(def string, props *d.RaceProperties) (raceStageAges, error) {
	var out raceStageAges
	humanlike := props.GetIntelligence() >= d.Intelligence_INTELLIGENCE_HUMANLIKE
	var adult, last float32
	adultFound := false
	for _, entry := range props.GetLifeStageAges() {
		age := entry.GetValue()
		stage := DefRow[*d.LifeStageDef](catalog, age.GetDef())
		if stage == nil {
			return out, contract("race %s has life stage %q with no row", def, age.GetDef())
		}
		ticks := raceTicks(age.GetMinAge())
		for _, flag := range []struct {
			into **int64
			on   bool
		}{{&out.Reproductive, stage.GetReproductive()}, {&out.Milkable, stage.GetMilkable()}, {&out.Shearable, stage.GetShearable()}} {
			if *flag.into == nil && flag.on {
				*flag.into = &ticks
			}
		}
		if !adultFound && int32(stage.GetDevelopmentalStage())&int32(d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT) != 0 {
			adult, adultFound = age.GetMinAge(), true
		}
		last = age.GetMinAge()
	}
	switch {
	case humanlike && adultFound:
		out.Adult = raceTicks(adult)
	case !humanlike:
		out.Adult = raceTicks(last)
	}
	return out, nil
}

// raceTrainables are the TrainableDefs the race can ever learn
// (Pawn_TrainingTracker.CanAssignToTrain's race rules: trainability rank,
// minimum body size and the tag lists), sorted.
func (catalog *DefinitionCatalog) raceTrainables(def string, props *d.RaceProperties) ([]string, error) {
	if props.GetTrainability() == "" {
		return nil, nil
	}
	rank := DefRow[*d.TrainabilityDef](catalog, props.GetTrainability())
	if rank == nil {
		return nil, contract("race %s has trainability %s with no row", def, props.GetTrainability())
	}
	var out []string
	for name, msg := range catalogDefs[*d.TrainableDef](catalog) {
		trainable := msg.(*d.TrainableDef)
		required := DefRow[*d.TrainabilityDef](catalog, trainable.GetRequiredTrainability())
		if required == nil || rank.GetIntelligenceOrder() < required.GetIntelligenceOrder() || props.GetBaseBodySize() < trainable.GetMinBodySize() {
			continue
		}
		matches := func(tag string) bool { return tag == name || slices.Contains(trainable.GetTags(), tag) }
		if slices.ContainsFunc(props.GetUntrainableTags(), matches) {
			continue
		}
		if tags := props.GetTrainableTags(); len(tags) == 0 || slices.ContainsFunc(tags, matches) {
			out = append(out, name)
		}
	}
	slices.SortFunc(out, strings.Compare)
	return out, nil
}
