package bridge

import (
	"slices"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// Stats a trait's offsets are read through (the game's StatDef names).
const (
	statWorkSpeed      = "WorkSpeedGlobal"
	statLearningFactor = "GlobalLearningFactor"
	statMoveSpeed      = "MoveSpeed"
	statRestRate       = "RestRateMultiplier"
)

// needOutdoors is the NeedDef a trait disables to keep its pawn out of the
// open (Outdoors); the needs a trait enables or disables are rows of the
// TraitDef.
const needOutdoors = "Outdoors"

// catalogDefs is every row of def class T by defName, nil when the catalog
// carries none.
func catalogDefs[T proto.Message](catalog *DefinitionCatalog) map[string]proto.Message {
	var zero T
	if catalog == nil {
		return nil
	}
	return catalog.Defs[zero.ProtoReflect().Descriptor().FullName()]
}

// float32Number is a def's float32 as the decimal the XML wrote (0.2, not
// 0.20000000298).
func float32Number(v float32) float64 {
	n, _ := strconv.ParseFloat(strconv.FormatFloat(float64(v), 'g', -1, 32), 64)
	return n
}

// TraitEffects is what a pawn's trait of the given degree does, read from
// the TraitDef row: the summed WorkSpeedGlobal, GlobalLearningFactor and
// MoveSpeed offsets of the degree, the work types its disabled work tags
// (and disabled work types) take away, a RestRateMultiplier offset above
// zero as a quick sleeper, the Outdoors need it disables as an
// undergrounder, and the human-meat ingestion thoughts it disallows as a
// cannibal. A trait or degree the catalog lacks is a contract error;
// effects the game applies in code and no row states come from
// policy.TraitFlags.
func (catalog *DefinitionCatalog) TraitEffects(name string, degree int) (policy.TraitEffects, error) {
	row := DefRow[*d.TraitDef](catalog, name)
	if row == nil {
		return policy.TraitEffects{}, contract("catalog has no trait %s", name)
	}
	var data *d.TraitDegreeData
	for _, entry := range row.GetDegreeDatas() {
		if int(entry.GetValue().GetDegree()) == degree {
			data = entry.GetValue()
			break
		}
	}
	if data == nil {
		return policy.TraitEffects{}, contract("catalog trait %s has no degree %d", name, degree)
	}
	var out policy.TraitEffects
	rest := 0.0
	for _, entry := range data.GetStatOffsets() {
		offset := float32Number(entry.GetValue().GetValue())
		switch entry.GetValue().GetStat() {
		case statWorkSpeed:
			out.WorkSpeed += offset
		case statLearningFactor:
			out.LearnRate += offset
		case statMoveSpeed:
			out.MoveSpeed += offset
		case statRestRate:
			rest += offset
		}
	}
	out.QuickSleeper = rest > 0
	for _, need := range data.GetDisablesNeeds() {
		if need == needOutdoors {
			out.Undergrounder = true
		}
	}
	for _, entry := range data.GetDisallowedThoughtsFromIngestion() {
		if entry.GetValue().GetMeatSource() == d.MeatSourceCategory_MEAT_SOURCE_CATEGORY_HUMANLIKE {
			out.Cannibal = true
		}
	}
	for work, def := range catalogDefs[*d.WorkTypeDef](catalog) {
		tags := def.(*d.WorkTypeDef).GetWorkTags()
		if row.GetDisabledWorkTags()&tags != 0 || slices.Contains(row.GetDisabledWorkTypes(), work) {
			out.DisabledWork = append(out.DisabledWork, policy.WorkType(work))
		}
	}
	slices.Sort(out.DisabledWork)
	return out.Add(policy.TraitFlags(name, degree)), nil
}
