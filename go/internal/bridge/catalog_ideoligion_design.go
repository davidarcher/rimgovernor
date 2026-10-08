package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"slices"
	"strings"
)

// IdeoligionOptions projects selection constraints from the same def mirror.
// It deliberately excludes unvalued stat/ability/apparel/mental-break effects.
func (catalog *DefinitionCatalog) IdeoligionOptions(faction string) (policy.DesignOptions, error) {
	var out policy.DesignOptions
	f := DefRow[*d.FactionDef](catalog, faction)
	if f == nil {
		return out, contract("design faction absent from catalog")
	}
	out.RequiredMemes = slices.Clone(f.GetRequiredMemes())
	for _, message := range catalogDefs[*d.MemeDef](catalog) {
		m := message.(*d.MemeDef)
		allowed := !m.GetHiddenInChooseMemes() && !slices.Contains(f.GetDisallowedMemes(), m.GetDefName()) && (len(f.GetAllowedMemes()) == 0 || slices.Contains(f.GetAllowedMemes(), m.GetDefName())) && (len(m.GetFactionWhitelist()) == 0 || slices.Contains(m.GetFactionWhitelist(), faction))
		if len(f.GetForcedMemes()) > 0 {
			allowed = slices.Contains(f.GetForcedMemes(), m.GetDefName())
		}
		if slices.Contains(f.GetRequiredMemes(), m.GetDefName()) {
			allowed = true
		}
		structure := m.GetCategory() == d.MemeCategory_MEME_CATEGORY_STRUCTURE
		if structure {
			for _, entry := range f.GetStructureMemeWeights() {
				w := entry.GetValue()
				if w.GetMeme() == m.GetDefName() && w.GetSelectionWeight() != 0 {
					allowed = true
				}
			}
		}
		v := policy.DesignMeme{Name: m.GetDefName(), Structure: structure, Allowed: allowed, InitialFluid: m.GetImpact() <= 2, Exclusions: slices.Clone(m.GetExclusionTags()), Supported: true, Obligations: len(m.GetRequiredRituals())}
		// These obligations require a choice of thing/species/trait not represented
		// by the plain-precept design. Do not price them as zero.
		if !strings.HasPrefix(strings.ToLower(m.GetModPackageId()), "ludeon.rimworld") || len(m.GetRequiredRituals()) > 0 || m.GetSelectOneOrNone() != nil || m.GetPreferredWeaponClasses() != nil || len(m.GetApparelRequirements()) > 0 || m.GetVeneratedAnimalsCountOffset() != 0 || m.GetVeneratedAnimalsCountOverride() > 0 {
			v.Supported = false
		}
		for _, group := range m.GetRequireOne() {
			if group.GetValue() == nil {
				return out, contract("null required precept group")
			}
			v.RequireOne = append(v.RequireOne, slices.Clone(group.GetValue().GetItems()))
		}
		for _, group := range v.RequireOne {
			for _, name := range group {
				p := DefRow[*d.PreceptDef](catalog, name)
				if p == nil {
					return out, contract("missing required precept")
				}
				if p.GetPreceptClass() != "RimWorld.Precept" {
					v.Supported = false
				}
			}
		}
		// A meme may unlock specialized roles without naming them in requireOne.
		// Their starting skill/ability feasibility is not a plain-precept cost.
		for _, message := range catalogDefs[*d.PreceptDef](catalog) {
			p := message.(*d.PreceptDef)
			if p.GetPreceptClass() != "RimWorld.Precept" && slices.Contains(p.GetRequiredMemes(), m.GetDefName()) {
				v.Supported = false
			}
		}
		out.Memes = append(out.Memes, v)
	}
	for _, message := range catalogDefs[*d.PreceptDef](catalog) {
		row := message.(*d.PreceptDef)
		if row.GetPreceptClass() != "RimWorld.Precept" {
			continue
		}
		issue := DefRow[*d.IssueDef](catalog, row.GetIssue())
		if issue == nil {
			return out, contract("missing design issue")
		}
		v := policy.DesignPrecept{Name: row.GetDefName(), Issue: row.GetIssue(), Default: row.GetDefaultSelectionWeight() > 0, Allowed: !row.GetClassicModeOnly() && !slices.Contains(f.GetDisallowedPrecepts(), row.GetDefName()), Supported: !issue.GetAllowMultiplePrecepts() && row.GetTakeNameFrom() == "" && row.GetAlsoAdds() == "" && len(row.GetStatOffsets()) == 0 && len(row.GetStatFactors()) == 0 && len(row.GetAbilityStatFactors()) == 0 && len(row.GetConditionalStatAffecters()) == 0, RequiredMemes: slices.Clone(row.GetRequiredMemes()), ConflictingMemes: slices.Clone(row.GetConflictingMemes()), Exclusions: slices.Clone(row.GetExclusionTags())}
		def, err := catalog.preceptDef(row)
		v.WorkTypes = slices.Clone(row.GetOpposedWorkTypes())
		if !strings.HasPrefix(strings.ToLower(row.GetModPackageId()), "ludeon.rimworld") {
			v.Supported = false
		}
		if err != nil {
			v.Supported = false
		} else {
			v.Score.Restrictions = len(row.GetOpposedWorkTypes())
			for _, effect := range def.Effects {
				if effect.Thought != "" {
					v.Thoughts = append(v.Thoughts, effect.Thought)
				}
				switch effect.Kind {
				case policy.EffectUnwilling:
					v.Score.Restrictions++
				case policy.EffectSelfTookAction, policy.EffectWitnessedAction, policy.EffectSituational, policy.EffectBed:
					var cost, benefit float64
					for _, mood := range effect.StageMoods {
						if -mood > cost {
							cost = -mood
						}
						if mood > benefit {
							benefit = mood
						}
					}
					v.Score.MoodCost += cost
					// A positive stage is a possible benefit, not evidence that the
					// starting colony can earn it. Preserve it during reform without
					// granting speculative credit to creation candidates.
					v.PotentialMoodBenefit += benefit
				default:
					v.Supported = false
				}
			}
		}
		out.Precepts = append(out.Precepts, v)
	}
	return out, nil
}
