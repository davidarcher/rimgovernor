package bridge

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The Ideology defs (#1760): policy.IdeologyDefs is a view over the catalog's
// generated rows (PreceptDef with its typed PreceptComps, RitualPatternDef,
// RitualBehaviorDef and the rows they name); nothing is read separately.

// classPreceptRole is the class of a role precept: a PreceptDef whose
// preceptClass is or derives from it is a role, not a plain precept.
const classPreceptRole = "RimWorld.Precept_Role"

// IdeologyDefs is the Ideology defs of the load, nil when the catalog has none
// (a game without Ideology). A def the rows name and the catalog lacks is an
// error, never a default.
func (catalog *DefinitionCatalog) IdeologyDefs() (*policy.IdeologyDefs, error) {
	if catalog == nil {
		return nil, nil
	}
	catalog.ideologyOnce.Do(func() { catalog.ideology, catalog.ideologyErr = catalog.buildIdeologyDefs() })
	return catalog.ideology, catalog.ideologyErr
}

func (catalog *DefinitionCatalog) buildIdeologyDefs() (*policy.IdeologyDefs, error) {
	preceptRows := catalogDefs[*d.PreceptDef](catalog)
	patternRows := catalogDefs[*d.RitualPatternDef](catalog)
	if len(preceptRows) == 0 && len(patternRows) == 0 {
		return nil, nil
	}
	out := &policy.IdeologyDefs{Precepts: map[string]policy.PreceptDef{}, Roles: map[string]policy.RoleDef{}, Rituals: map[string]policy.RitualDef{}}
	for name, msg := range preceptRows {
		row := msg.(*d.PreceptDef)
		role := false
		if class := row.GetPreceptClass(); class != "" {
			var err error
			if role, err = catalog.ClassIsA(class, classPreceptRole); err != nil {
				return nil, err
			}
		}
		if role {
			def, err := roleDef(row)
			if err != nil {
				return nil, err
			}
			out.Roles[name] = def
			continue
		}
		def, err := catalog.preceptDef(row)
		if err != nil {
			return nil, err
		}
		out.Precepts[name] = def
	}
	for name, msg := range patternRows {
		def, err := catalog.ritualDef(msg.(*d.RitualPatternDef))
		if err != nil {
			return nil, err
		}
		out.Rituals[name] = def
	}
	return out, nil
}

// preceptDef is a precept def with one effect per PreceptComp, in comp order.
func (catalog *DefinitionCatalog) preceptDef(row *d.PreceptDef) (policy.PreceptDef, error) {
	def := policy.PreceptDef{Name: row.GetDefName()}
	for _, entry := range row.GetComps() {
		comp := entry.GetValue()
		if comp == nil || comp.GetValue() == nil {
			return policy.PreceptDef{}, contract("precept %s has a null comp", def.Name)
		}
		effect, err := catalog.preceptEffect(comp)
		if err != nil {
			return policy.PreceptDef{}, contract("precept %s: %v", def.Name, err)
		}
		def.Effects = append(def.Effects, effect)
	}
	return def, nil
}

// thoughtMoods is the base mood effect of each stage of the named thought; no
// thought is no stage. A null stage (the game keeps unused stages null) is a
// stage of no mood.
func (catalog *DefinitionCatalog) thoughtMoods(name string) ([]float64, error) {
	if name == "" {
		return nil, nil
	}
	row, err := thoughtRow(catalog, name)
	if err != nil {
		return nil, err
	}
	moods := make([]float64, 0, len(row.GetStages()))
	for _, stage := range row.GetStages() {
		moods = append(moods, float32Number(stage.GetValue().GetBaseMoodEffect()))
	}
	return moods, nil
}

// preceptEffect types one PreceptComp by the oneof arm the mirror filled: a
// thought comp carries its thought's stage moods, an event comp the history
// event it reacts to, an unwilling comp the traits and hediffs that cancel it.
func (catalog *DefinitionCatalog) preceptEffect(comp *d.PreceptCompAny) (policy.PreceptEffect, error) {
	var out policy.PreceptEffect
	thought := func(name string) (err error) {
		out.StageMoods, err = catalog.thoughtMoods(name)
		return err
	}
	unwilling := func(event string, traits []*d.Opt_TraitRequirement, hediffs []string) error {
		out.Kind, out.HistoryEvent = policy.EffectUnwilling, event
		for _, t := range traits {
			if t.GetValue() == nil {
				return contract("unwilling comp has a null trait requirement")
			}
			out.NullifyingTraits = append(out.NullifyingTraits, t.GetValue().GetDef())
		}
		out.NullifyingHediffs = slices.Clone(hediffs)
		sort.Strings(out.NullifyingTraits)
		sort.Strings(out.NullifyingHediffs)
		return nil
	}
	switch c := comp.GetValue().(type) {
	case *d.PreceptCompAny_PreceptComp_SelfTookMemoryThought:
		out.Kind, out.HistoryEvent, out.OnlyForNonSlaves = policy.EffectSelfTookAction, c.PreceptComp_SelfTookMemoryThought.GetEventDef(), c.PreceptComp_SelfTookMemoryThought.GetOnlyForNonSlaves()
		return out, thought(c.PreceptComp_SelfTookMemoryThought.GetThought())
	case *d.PreceptCompAny_PreceptComp_KnowsMemoryThought:
		out.Kind, out.HistoryEvent = policy.EffectWitnessedAction, c.PreceptComp_KnowsMemoryThought.GetEventDef()
		return out, thought(c.PreceptComp_KnowsMemoryThought.GetThought())
	case *d.PreceptCompAny_PreceptComp_SituationalThought:
		out.Kind = policy.EffectSituational
		return out, thought(c.PreceptComp_SituationalThought.GetThought())
	case *d.PreceptCompAny_PreceptComp_BedThought:
		out.Kind = policy.EffectBed
		return out, thought(c.PreceptComp_BedThought.GetThought())
	case *d.PreceptCompAny_PreceptComp_UnwillingToDo:
		m := c.PreceptComp_UnwillingToDo
		return out, unwilling(m.GetEventDef(), m.GetNullifyingTraits(), m.GetNullifyingHediffs())
	case *d.PreceptCompAny_PreceptComp_UnwillingToDo_Chance:
		m := c.PreceptComp_UnwillingToDo_Chance
		out.Chance = domain.Known(float32Number(m.GetChance()))
		return out, unwilling(m.GetEventDef(), m.GetNullifyingTraits(), m.GetNullifyingHediffs())
	case *d.PreceptCompAny_PreceptComp_UnwillingToDo_Gendered:
		m := c.PreceptComp_UnwillingToDo_Gendered
		return out, unwilling(m.GetEventDef(), m.GetNullifyingTraits(), m.GetNullifyingHediffs())
	case *d.PreceptCompAny_PreceptComp_UnwillingToDo_WithDef:
		m := c.PreceptComp_UnwillingToDo_WithDef
		return out, unwilling(m.GetEventDef(), m.GetNullifyingTraits(), m.GetNullifyingHediffs())
	case *d.PreceptCompAny_PreceptComp_Apparel, *d.PreceptCompAny_PreceptComp_Apparel_Desired, *d.PreceptCompAny_PreceptComp_Apparel_DesiredStrong:
		out.Kind = policy.EffectApparel
	case *d.PreceptCompAny_PreceptComp_MentalBreak:
		out.Kind = policy.EffectMentalBreak
	case *d.PreceptCompAny_PreceptComp_DevelopmentPoints:
		out.Kind, out.HistoryEvent = policy.EffectDevelopmentPoints, c.PreceptComp_DevelopmentPoints.GetEventDef()
	case *d.PreceptCompAny_PreceptComp_GoodwillSituation:
		out.Kind = policy.EffectGoodwillSituation
	default:
		return out, contract("precept comp %T has no effect kind", c)
	}
	return out, nil
}

// roleDef is a role precept def: how many pawns hold it and the skills each
// requirement asks for (a RoleRequirement_MinSkillAny lists the skills of
// which one must meet its minimum; other requirements have none).
func roleDef(row *d.PreceptDef) (policy.RoleDef, error) {
	def := policy.RoleDef{Name: row.GetDefName(), MaxCount: int(row.GetMaxCount())}
	for _, entry := range row.GetRoleRequirements() {
		requirement := entry.GetValue()
		if requirement == nil || requirement.GetValue() == nil {
			return policy.RoleDef{}, contract("role %s has a null requirement", def.Name)
		}
		var out policy.RoleRequirement
		if any := requirement.GetRoleRequirement_MinSkillAny(); any != nil {
			for _, s := range any.GetSkills() {
				if s.GetValue() == nil {
					return policy.RoleDef{}, contract("role %s has a null skill requirement", def.Name)
				}
				out.Skills = append(out.Skills, policy.SkillRequirement{Skill: s.GetValue().GetSkill(), MinLevel: int(s.GetValue().GetMinLevel())})
			}
		}
		def.Requirements = append(def.Requirements, out)
	}
	return def, nil
}

// ritualDef is a ritual pattern: its cadence, whether it may start freely, the
// buildings its obligation target filter accepts and its behavior's role slots.
func (catalog *DefinitionCatalog) ritualDef(row *d.RitualPatternDef) (policy.RitualDef, error) {
	name := row.GetDefName()
	def := policy.RitualDef{Name: name, IntervalDaysMin: float32Number(row.GetRitualFreeStartIntervalDaysRange().GetMin()),
		CanStartAnytime: row.GetCanStartAnytime(), AlwaysStartAnytime: row.GetAlwaysStartAnytime()}
	if filter := row.GetRitualObligationTargetFilter(); filter != "" {
		target := DefRow[*d.RitualObligationTargetFilterDef](catalog, filter)
		if target == nil {
			return policy.RitualDef{}, contract("ritual %s: catalog has no obligation target filter %s", name, filter)
		}
		def.RequiredBuildings = slices.Clone(target.GetThingDefs())
		sort.Strings(def.RequiredBuildings)
	}
	if behavior := row.GetRitualBehavior(); behavior != "" {
		rows := DefRow[*d.RitualBehaviorDef](catalog, behavior)
		if rows == nil {
			return policy.RitualDef{}, contract("ritual %s: catalog has no behavior %s", name, behavior)
		}
		for _, entry := range rows.GetRoles() {
			slot, err := ritualSlot(entry)
			if err != nil {
				return policy.RitualDef{}, contract("ritual %s behavior %s: %v", name, behavior, err)
			}
			def.Roles = append(def.Roles, slot)
		}
	}
	return def, nil
}

// ritualSlot reads a RitualRole's id, precept, maxCount and required, the
// fields every concrete role class shares, from whichever class the mirror
// filled.
func ritualSlot(entry *d.Opt_RitualRoleAny) (policy.RitualRoleSlot, error) {
	if entry.GetValue() == nil {
		return policy.RitualRoleSlot{}, contract("null role")
	}
	msg := entry.GetValue().ProtoReflect()
	which := msg.WhichOneof(msg.Descriptor().Oneofs().ByName("value"))
	if which == nil {
		return policy.RitualRoleSlot{}, contract("role of no class")
	}
	role := msg.Get(which).Message()
	fields := role.Descriptor().Fields()
	id, precept, maxCount, required := fields.ByName("id"), fields.ByName("precept"), fields.ByName("maxCount"), fields.ByName("required")
	if id == nil || precept == nil || maxCount == nil || required == nil {
		return policy.RitualRoleSlot{}, contract("role class %s lacks a slot field", role.Descriptor().FullName())
	}
	return policy.RitualRoleSlot{ID: role.Get(id).String(), Precept: role.Get(precept).String(), MaxCount: int(role.Get(maxCount).Int()), Required: role.Get(required).Bool()}, nil
}
