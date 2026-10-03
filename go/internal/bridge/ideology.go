package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The Ideology facts (#1654): static defs in the definition catalog, the
// primary ideoligion in the frame's ideology section. A row that is not
// valid fails the whole decode: nothing is skipped or defaulted.

var effectKinds = map[o.PreceptEffectKind]policy.PreceptEffectKind{
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_SELF_TOOK_ACTION:   policy.EffectSelfTookAction,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_WITNESSED_ACTION:   policy.EffectWitnessedAction,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_SITUATIONAL:        policy.EffectSituational,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_BED:                policy.EffectBed,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_UNWILLING:          policy.EffectUnwilling,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_APPAREL:            policy.EffectApparel,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_MENTAL_BREAK:       policy.EffectMentalBreak,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_DEVELOPMENT_POINTS: policy.EffectDevelopmentPoints,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_GOODWILL_SITUATION: policy.EffectGoodwillSituation,
	o.PreceptEffectKind_PRECEPT_EFFECT_KIND_OTHER:              policy.EffectOther,
}

// validIDs reports whether every id is valid; unique also refuses a repeat.
func validIDs(ids []string, unique bool) bool {
	seen := map[string]bool{}
	for _, id := range ids {
		if validID(id) != nil || unique && seen[id] {
			return false
		}
		seen[id] = true
	}
	return true
}

func optionalID(id *string) bool { return id == nil || validID(*id) == nil }

func finite(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) }

// DecodeIdeologyCatalog validates the catalog's Ideology defs; nil for a
// catalog without them.
func DecodeIdeologyCatalog(v *o.IdeologyCatalog) (*policy.IdeologyDefs, error) {
	if v == nil {
		return nil, nil
	}
	out := &policy.IdeologyDefs{Memes: map[string]policy.MemeDef{}, Precepts: map[string]policy.PreceptDef{}, Roles: map[string]policy.RoleDef{}, Rituals: map[string]policy.RitualDef{}}
	for _, row := range v.Memes {
		name := row.GetDefName()
		if _, dup := out.Memes[name]; validID(name) != nil || dup || row.Category == nil || validID(row.GetCategory()) != nil || row.Impact == nil || !validIDs(row.ExclusionTags, false) {
			return nil, contract("invalid or duplicate ideology meme %q", name)
		}
		meme := policy.MemeDef{Name: name, Category: row.GetCategory(), Impact: int(row.GetImpact()), ExclusionTags: row.ExclusionTags, ConsumableBuildings: row.ConsumableBuildings, RitualSeats: row.RitualSeats}
		if !validIDs(row.ConsumableBuildings, false) || !validIDs(row.RitualSeats, false) {
			return nil, contract("invalid ideology meme %s building", name)
		}
		for _, r := range row.RequiredRituals {
			if r == nil || !optionalID(r.Precept) || !optionalID(r.Pattern) || !optionalID(r.Building) {
				return nil, contract("invalid ideology meme %s required ritual", name)
			}
			meme.RequiredRituals = append(meme.RequiredRituals, policy.RequiredRitual{Precept: r.GetPrecept(), Pattern: r.GetPattern(), Building: r.GetBuilding()})
		}
		out.Memes[name] = meme
	}
	for _, row := range v.Precepts {
		def, err := decodePreceptDef(row)
		if err != nil {
			return nil, err
		}
		if _, dup := out.Precepts[def.Name]; dup {
			return nil, contract("duplicate ideology precept %s", def.Name)
		}
		out.Precepts[def.Name] = def
	}
	for _, row := range v.Roles {
		def, err := decodeRoleDef(row)
		if err != nil {
			return nil, err
		}
		if _, dup := out.Roles[def.Name]; dup {
			return nil, contract("duplicate ideology role %s", def.Name)
		}
		if _, dup := out.Precepts[def.Name]; dup {
			return nil, contract("ideology role %s is also a precept", def.Name)
		}
		out.Roles[def.Name] = def
	}
	for _, row := range v.Rituals {
		def, err := decodeRitualDef(row)
		if err != nil {
			return nil, err
		}
		if _, dup := out.Rituals[def.Name]; dup {
			return nil, contract("duplicate ideology ritual %s", def.Name)
		}
		out.Rituals[def.Name] = def
	}
	return out, nil
}

func decodePreceptDef(row *o.PreceptDefinition) (policy.PreceptDef, error) {
	name := row.GetDefName()
	if validID(name) != nil || row.Impact == nil || validID(row.GetImpact()) != nil || !optionalID(row.Issue) || !optionalID(row.PreceptClass) {
		return policy.PreceptDef{}, contract("invalid ideology precept %q", name)
	}
	for _, list := range [][]string{row.RequiredMemes, row.ConflictingMemes, row.AssociatedMemes, row.ExclusionTags, row.Flags} {
		if !validIDs(list, false) {
			return policy.PreceptDef{}, contract("invalid ideology precept %s reference", name)
		}
	}
	def := policy.PreceptDef{Name: name, Issue: row.GetIssue(), IssueAllowsMultiple: row.GetIssueAllowsMultiple(), Impact: row.GetImpact(), Class: row.GetPreceptClass(),
		RequiredMemes: row.RequiredMemes, ConflictingMemes: row.ConflictingMemes, AssociatedMemes: row.AssociatedMemes, ExclusionTags: row.ExclusionTags, Flags: row.Flags,
		RitualPattern: row.GetRitualPattern(), MaxCount: int(row.GetMaxCount())}
	if !optionalID(row.RitualPattern) {
		return policy.PreceptDef{}, contract("invalid ideology precept %s ritual pattern", name)
	}
	for _, b := range row.Buildings {
		if b == nil || validID(b.GetBuilding()) != nil || b.Chance == nil || !finite(b.GetChance()) || b.GetChance() < 0 {
			return policy.PreceptDef{}, contract("invalid ideology precept %s building", name)
		}
		def.Buildings = append(def.Buildings, policy.PreceptBuilding{Building: b.GetBuilding(), Chance: b.GetChance()})
	}
	for _, e := range row.Effects {
		kind, ok := effectKinds[e.GetKind()]
		if !ok || validID(e.GetCompClass()) != nil || !optionalID(e.HistoryEvent) || !optionalID(e.Thought) || !optionalID(e.Gender) || !optionalID(e.Building) || !optionalID(e.MentalBreak) ||
			!optionalID(e.GoodwillSituation) || !validIDs(e.NullifyingTraits, false) || !validIDs(e.NullifyingHediffs, false) || e.Chance != nil && (!finite(e.GetChance()) || e.GetChance() < 0) {
			return policy.PreceptDef{}, contract("invalid ideology precept %s effect", name)
		}
		for _, mood := range e.StageMoods {
			if !finite(mood) {
				return policy.PreceptDef{}, contract("invalid ideology precept %s effect mood", name)
			}
		}
		def.Effects = append(def.Effects, policy.PreceptEffect{Kind: kind, CompClass: e.GetCompClass(), HistoryEvent: e.GetHistoryEvent(), Thought: e.GetThought(), StageMoods: e.StageMoods,
			OnlyForNonSlaves: e.GetOnlyForNonSlaves(), NullifyingTraits: e.NullifyingTraits, NullifyingHediffs: e.NullifyingHediffs, Chance: optionalFact(e.Chance),
			Gender: e.GetGender(), Building: e.GetBuilding(), MentalBreak: e.GetMentalBreak(), GoodwillSituation: e.GetGoodwillSituation()})
	}
	return def, nil
}

func decodeRoleDef(row *o.RoleDefinition) (policy.RoleDef, error) {
	name := row.GetDefName()
	if validID(name) != nil || row.MaxCount == nil || row.ActivationBelieverCount == nil || row.DeactivationBelieverCount == nil {
		return policy.RoleDef{}, contract("invalid ideology role %q", name)
	}
	for _, list := range [][]string{row.DisabledWorkTags, row.RequiredWorkTags, row.RequiredWorkTagAny, row.GrantedAbilities, row.Tags} {
		if !validIDs(list, false) {
			return policy.RoleDef{}, contract("invalid ideology role %s reference", name)
		}
	}
	def := policy.RoleDef{Name: name, Leader: row.GetLeader(), MaxCount: int(row.GetMaxCount()), ActivationBelievers: int(row.GetActivationBelieverCount()), DeactivationBelievers: int(row.GetDeactivationBelieverCount()),
		DisabledWorkTags: row.DisabledWorkTags, RequiredWorkTags: row.RequiredWorkTags, RequiredWorkTagAny: row.RequiredWorkTagAny, GrantedAbilities: row.GrantedAbilities, Tags: row.Tags}
	for _, r := range row.Requirements {
		if r == nil || validID(r.GetRequirementClass()) != nil {
			return policy.RoleDef{}, contract("invalid ideology role %s requirement", name)
		}
		requirement := policy.RoleRequirement{Class: r.GetRequirementClass()}
		for _, s := range r.Skills {
			if s == nil || validID(s.GetSkill()) != nil || s.MinLevel == nil {
				return policy.RoleDef{}, contract("invalid ideology role %s skill requirement", name)
			}
			requirement.Skills = append(requirement.Skills, policy.SkillRequirement{Skill: s.GetSkill(), MinLevel: int(s.GetMinLevel())})
		}
		def.Requirements = append(def.Requirements, requirement)
	}
	for _, e := range row.Effects {
		if e == nil || validID(e.GetEffectClass()) != nil || e.Bad == nil || !optionalID(e.Stat) || e.Modifier != nil && !finite(e.GetModifier()) {
			return policy.RoleDef{}, contract("invalid ideology role %s effect", name)
		}
		def.Effects = append(def.Effects, policy.RoleEffect{Class: e.GetEffectClass(), Bad: e.GetBad(), Stat: e.GetStat(), Modifier: optionalFact(e.Modifier)})
	}
	return def, nil
}

func decodeRitualDef(row *o.RitualDefinition) (policy.RitualDef, error) {
	name := row.GetDefName()
	if validID(name) != nil || row.IntervalDaysMin == nil || row.IntervalDaysMax == nil || !finite(row.GetIntervalDaysMin()) || !finite(row.GetIntervalDaysMax()) || row.GetIntervalDaysMin() > row.GetIntervalDaysMax() ||
		!optionalID(row.ObligationTargetFilter) || !optionalID(row.TargetFilter) || !optionalID(row.Behavior) || !optionalID(row.MinTechLevel) || !optionalID(row.MaxTechLevel) ||
		!validIDs(row.ObligationTriggers, false) || !validIDs(row.RequiredBuildings, false) {
		return policy.RitualDef{}, contract("invalid ideology ritual %q", name)
	}
	def := policy.RitualDef{Name: name, IntervalDaysMin: row.GetIntervalDaysMin(), IntervalDaysMax: row.GetIntervalDaysMax(), ObligationTriggers: row.ObligationTriggers,
		ObligationTargetFilter: row.GetObligationTargetFilter(), RequiredBuildings: row.RequiredBuildings, TargetFilter: row.GetTargetFilter(), Behavior: row.GetBehavior(),
		CanStartAnytime: row.GetCanStartAnytime(), AlwaysStartAnytime: row.GetAlwaysStartAnytime(), IdeoMembersOnly: row.GetIdeoMembersOnly(), MinTechLevel: row.GetMinTechLevel(), MaxTechLevel: row.GetMaxTechLevel()}
	slots := map[string]bool{}
	for _, r := range row.Roles {
		if r == nil || validID(r.GetId()) != nil || slots[r.GetId()] || !optionalID(r.Precept) || r.MaxCount == nil || r.Required == nil {
			return policy.RitualDef{}, contract("invalid ideology ritual %s role slot", name)
		}
		slots[r.GetId()] = true
		def.Roles = append(def.Roles, policy.RitualRoleSlot{ID: r.GetId(), Precept: r.GetPrecept(), MaxCount: int(r.GetMaxCount()), Required: r.GetRequired()})
	}
	return def, nil
}

// DecodeIdeology validates the frame's ideology section under identity and
// resolves every def name in it against defs, the catalog's Ideology defs:
// a name the catalog lacks, or a section without catalog defs, is a
// contract failure.
func DecodeIdeology(v *o.IdeologySnapshot, identity *c.Identity, defs *policy.IdeologyDefs) (*policy.Ideoligion, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return nil, contract("invalid ideology context")
	}
	if defs == nil {
		return nil, contract("ideology section without catalog ideology defs")
	}
	if validID(v.GetIdeoId()) != nil || v.ObligationsActive == nil || v.Believers == nil || v.GetBelievers() < 0 || v.MinBelieversForObligations == nil || v.GetMinBelieversForObligations() < 0 {
		return nil, contract("invalid ideology section")
	}
	facts := policy.IdeoligionFacts{IdeoID: v.GetIdeoId(), Memes: v.Memes, ObligationsActive: v.GetObligationsActive(), Believers: int(v.GetBelievers()), MinBelievers: int(v.GetMinBelieversForObligations())}
	for _, meme := range v.Memes {
		if _, ok := defs.Memes[meme]; !ok {
			return nil, contract("ideology meme %q is not in the catalog", meme)
		}
	}
	ids := map[string]bool{}
	claim := func(id string) error {
		if validID(id) != nil || ids[id] {
			return contract("invalid or duplicate ideology precept id %q", id)
		}
		ids[id] = true
		return nil
	}
	precept := func(id, def string) error {
		if err := claim(id); err != nil {
			return err
		}
		if _, ok := defs.Precepts[def]; !ok {
			return contract("ideology precept %q is not in the catalog", def)
		}
		return nil
	}
	for _, row := range v.Precepts {
		if err := precept(row.GetId(), row.GetDefName()); err != nil {
			return nil, err
		}
		facts.Precepts = append(facts.Precepts, policy.HeldPrecept{ID: row.GetId(), Def: row.GetDefName()})
	}
	for _, row := range v.Roles {
		if err := claim(row.GetId()); err != nil {
			return nil, err
		}
		if _, ok := defs.Roles[row.GetDefName()]; !ok || row.Active == nil {
			return nil, contract("ideology role %q is not in the catalog or lacks its state", row.GetDefName())
		}
		held := policy.HeldRole{ID: row.GetId(), Def: row.GetDefName(), Active: row.GetActive()}
		seen := map[string]bool{}
		for _, ref := range row.Pawns {
			if !validRef(ref) || seen[ref.GetId()] {
				return nil, contract("invalid or duplicate ideology role pawn")
			}
			seen[ref.GetId()] = true
			held.Pawns = append(held.Pawns, domain.PawnID(ref.GetId()))
		}
		facts.Roles = append(facts.Roles, held)
	}
	for _, row := range v.Rituals {
		if err := precept(row.GetId(), row.GetDefName()); err != nil {
			return nil, err
		}
		if !optionalID(row.Pattern) || row.LastFinishedTick == nil || row.ActiveObligations == nil || row.GetActiveObligations() < 0 || row.RepeatPenaltyActive == nil {
			return nil, contract("invalid ideology ritual %q", row.GetId())
		}
		if _, ok := defs.Rituals[row.GetPattern()]; row.Pattern != nil && !ok {
			return nil, contract("ideology ritual pattern %q is not in the catalog", row.GetPattern())
		}
		facts.Rituals = append(facts.Rituals, policy.HeldRitual{ID: row.GetId(), Def: row.GetDefName(), Pattern: row.GetPattern(), LastFinishedTick: int64(row.GetLastFinishedTick()),
			ActiveObligations: int(row.GetActiveObligations()), RepeatPenaltyActive: row.GetRepeatPenaltyActive()})
	}
	for _, row := range v.Buildings {
		if err := precept(row.GetId(), row.GetDefName()); err != nil {
			return nil, err
		}
		if !optionalID(row.Building) {
			return nil, contract("invalid ideology building %q", row.GetId())
		}
		facts.Buildings = append(facts.Buildings, policy.HeldBuilding{ID: row.GetId(), Def: row.GetDefName(), Building: row.GetBuilding()})
	}
	return &policy.Ideoligion{Defs: *defs, Facts: facts}, nil
}
