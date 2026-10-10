package bridge

import (
	"slices"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The game's recipe workers that tell a replacement part from an implant:
// a natural install gives the organ back, an artificial install adds a
// Hediff_AddedPart whose addedPartProps.partEfficiency is the capacity it
// gives. Every other worker (Recipe_InstallImplant, Recipe_AddHediff) adds no
// replacement part.
const (
	naturalPartWorker    = "RimWorld.Recipe_InstallNaturalBodyPart"
	artificialPartWorker = "RimWorld.Recipe_InstallArtificialBodyPart"
)

// PartTier is the capacity the install recipe's part gives back relative to a
// natural part (1): 1 for a natural install, the added part hediff's
// partEfficiency for an artificial one, 0 for a recipe that installs no
// replacement part. A nil catalog has none (a test without a loaded game); a
// recipe with no row, or an artificial install whose hediff has no row or no
// addedPartProps, is an error.
func (catalog *DefinitionCatalog) PartTier(recipe string) (float64, error) {
	if catalog == nil {
		return 0, nil
	}
	row, err := catalog.Recipe(recipe)
	if err != nil {
		return 0, err
	}
	switch row.GetWorkerClass() {
	case naturalPartWorker:
		return 1, nil
	case artificialPartWorker:
		return catalog.HediffPartTier(row.GetAddsHediff())
	}
	return 0, nil
}

// HediffPartTier is the added part hediff's partEfficiency. A nil catalog has
// none; a hediff with no row or no addedPartProps is an error.
func (catalog *DefinitionCatalog) HediffPartTier(hediff string) (float64, error) {
	if catalog == nil {
		return 0, nil
	}
	row := DefRow[*d.HediffDef](catalog, hediff)
	if row == nil {
		return 0, contract("catalog has no hediff row for %s", hediff)
	}
	if row.GetAddedPartProps() == nil {
		return 0, contract("hediff %s has no addedPartProps", hediff)
	}
	return decimal32(row.GetAddedPartProps().GetPartEfficiency()), nil
}

// SeriousBloodLoss is the BloodLoss severity from which a pawn is seriously
// bleeding: the minSeverity of the hediff's "moderate" stage, where blood loss
// first costs consciousness heavily. Unknown for a nil catalog or one with no
// BloodLoss row; a BloodLoss row with no such stage is an error.
func (catalog *DefinitionCatalog) SeriousBloodLoss() (domain.Fact[float64], error) {
	row := DefRow[*d.HediffDef](catalog, bloodLossHediff)
	if row == nil {
		return domain.Unknown[float64](), nil
	}
	for _, stage := range row.GetStages() {
		if stage.GetValue().GetLabel() == bloodLossSeriousStage {
			return domain.Known(decimal32(stage.GetValue().GetMinSeverity())), nil
		}
	}
	return domain.Unknown[float64](), contract("hediff %s has no %q stage", bloodLossHediff, bloodLossSeriousStage)
}

// HediffBad is the hediff def's isBad. A nil catalog has none; a hediff with no
// row is an error.
func (catalog *DefinitionCatalog) HediffBad(hediff string) (domain.Fact[bool], error) {
	if catalog == nil {
		return domain.Unknown[bool](), nil
	}
	row := DefRow[*d.HediffDef](catalog, hediff)
	if row == nil {
		return domain.Unknown[bool](), contract("catalog has no hediff row for %s", hediff)
	}
	return domain.Known(row.GetIsBad()), nil
}

// HediffSpawnOnRemoved is the item a removed part hediff leaves behind (its
// spawnThingOnRemoved), unknown when it spawns nothing or the catalog is nil; a
// hediff with no row is an error.
func (catalog *DefinitionCatalog) HediffSpawnOnRemoved(hediff string) (domain.Fact[policy.Resource], error) {
	if catalog == nil {
		return domain.Unknown[policy.Resource](), nil
	}
	row := DefRow[*d.HediffDef](catalog, hediff)
	if row == nil {
		return domain.Unknown[policy.Resource](), contract("catalog has no hediff row for %s", hediff)
	}
	if row.GetSpawnThingOnRemoved() == "" {
		return domain.Unknown[policy.Resource](), nil
	}
	return domain.Known(policy.Resource(row.GetSpawnThingOnRemoved())), nil
}

const (
	bloodLossHediff       = "BloodLoss"
	bloodLossSeriousStage = "moderate"
)

// bodyPartFacts derives the harvest and kept-part facts from the BodyDef trees
// and the BodyPartDef rows. A vital BodyPartTagDef marks a part vital; a part is a vital part when removing one instance kills in every body that has it, and a vital tag rides on
// it. Removing one instance of it kills when some vital tag of it is provided
// by no other part of that body (a heart is the only blood pump); a vital part
// whose every vital tag survives the removal of one instance and which the body
// carries twice is a paired organ, a harvest organ when it spawns the item of
// its own defName on removal (a kidney).
func (catalog *DefinitionCatalog) bodyPartFacts() (organs []string, vital map[string]bool) {
	vitalTag := map[string]bool{}
	for name, row := range catalog.Defs[(&d.BodyPartTagDef{}).ProtoReflect().Descriptor().FullName()] {
		if tag, ok := row.(*d.BodyPartTagDef); ok && tag.GetVital() {
			vitalTag[name] = true
		}
	}
	parts := map[string]*d.BodyPartDef{}
	for name, row := range catalog.Defs[(&d.BodyPartDef{}).ProtoReflect().Descriptor().FullName()] {
		if part, ok := row.(*d.BodyPartDef); ok {
			parts[name] = part
		}
	}
	type tally struct {
		bodies, killing int
		hasVital, pair  bool
	}
	tallies := map[string]*tally{}
	for _, row := range catalog.Defs[(&d.BodyDef{}).ProtoReflect().Descriptor().FullName()] {
		body, ok := row.(*d.BodyDef)
		if !ok {
			continue
		}
		count := map[string]int{}
		provided := map[string]int{}
		var walk func(*d.BodyPartRecord)
		walk = func(record *d.BodyPartRecord) {
			count[record.GetDef()]++
			for _, tag := range parts[record.GetDef()].GetTags() {
				provided[tag]++
			}
			for _, child := range record.GetParts() {
				walk(child.GetValue())
			}
		}
		walk(body.GetCorePart())
		for name, n := range count {
			t := tallies[name]
			if t == nil {
				t = &tally{}
				tallies[name] = t
			}
			t.bodies++
			t.pair = t.pair || n >= 2
			var kills bool
			for _, tag := range parts[name].GetTags() {
				if vitalTag[tag] {
					t.hasVital = true
					kills = kills || provided[tag] <= 1
				}
			}
			if kills {
				t.killing++
			}
		}
	}
	vital = map[string]bool{}
	for name, t := range tallies {
		switch {
		case t.killing == t.bodies:
			vital[name] = true
		case t.hasVital && t.pair && parts[name].GetSpawnThingOnRemoved() == name:
			organs = append(organs, name)
		}
	}
	slices.Sort(organs)
	return organs, vital
}

// decimal32 widens a def row's float to the decimal the XML wrote (0.6, not
// 0.6000000238).
func decimal32(f float32) float64 {
	v, _ := strconv.ParseFloat(strconv.FormatFloat(float64(f), 'g', -1, 32), 64)
	return v
}
