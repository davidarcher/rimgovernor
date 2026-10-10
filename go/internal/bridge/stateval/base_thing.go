package stateval

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The thing-side terms of the base StatWorker.GetValueUnfinalized (epic
// #2621, #2639): the pawn's skill, capacity, trait, hediff, ideology, gene,
// life stage and gear offsets and factors, the stuff's quality terms, and for
// any thing the comps' stat offsets and factors, the stat's statFactors and
// the pawn's skill and capacity factors and inspiration. Every live input is a
// Known fact of the request's StatContext (context_base.go); def-side data is
// read from the mirrored rows.

const (
	modBiotech = "ludeon.rimworld.biotech"

	classCompUniqueWeapon = "RimWorld.CompUniqueWeapon"
	classCompFacilities   = "RimWorld.CompAffectedByFacilities"
	classCompBladelink    = "RimWorld.CompBladelinkWeapon"
	classCompBiocodable   = "RimWorld.CompBiocodable"
	classMinifiedThing    = "RimWorld.MinifiedThing"

	statMaxHitPoints = "MaxHitPoints"
)

// floatEpsilon is C#'s float.Epsilon, the smallest positive float32.
const floatEpsilon = math.SmallestNonzeroFloat32

// ctx is the request's StatContext, nil for a definition request
// (StatRequest.HasThing false).
func (r *Request) ctx() *StatContext { return r.Subject.Context }

// statValue is Thing.GetStatValue(stat) of the request's own thing: the same
// context evaluated for another stat.
func (r *Request) statValue(stat string) (float32, error) {
	return r.Evaluator.Value(stat, r.Subject)
}

// qualityState is the thing's TryGetQuality, or not-Has for a definition
// request.
func (r *Request) qualityState() (QualityState, error) {
	c := r.ctx()
	if c == nil {
		return QualityState{}, nil
	}
	return need(c.Base.Quality, "the thing's quality (TryGetQuality)")
}

// qualityCategory is StatRequest.QualityCategory: the thing's quality, Normal
// when it has none; a definition request states its own.
func (r *Request) qualityCategory() (int32, error) {
	if r.ctx() == nil {
		return r.Quality, nil
	}
	q, err := r.qualityState()
	if err != nil {
		return 0, err
	}
	if !q.Has {
		return 2, nil
	}
	return q.Category, nil
}

// qualityListValue is StatUtility.GetStatValueFromList over a quality list.
func qualityListValue(list []*d.Opt_StatModifierQuality, stat string, qc int32, def float32) (float32, error) {
	for _, m := range list {
		mod := m.GetValue()
		if mod == nil {
			return 0, fmt.Errorf("empty stat modifier quality entry")
		}
		if mod.GetStat() != stat {
			continue
		}
		switch qc {
		case 0:
			return mod.GetAwful(), nil
		case 1:
			return mod.GetPoor(), nil
		case 2:
			return mod.GetNormal(), nil
		case 3:
			return mod.GetGood(), nil
		case 4:
			return mod.GetExcellent(), nil
		case 5:
			return mod.GetMasterwork(), nil
		case 6:
			return mod.GetLegendary(), nil
		}
		return 0, fmt.Errorf("quality %d is not a quality category", qc)
	}
	return def, nil
}

func statMods(list []StatMod) []*d.Opt_StatModifier {
	out := make([]*d.Opt_StatModifier, len(list))
	for i, m := range list {
		out[i] = &d.Opt_StatModifier{Value: &d.StatModifier{Stat: m.Stat, Value: m.Value}}
	}
	return out
}

// thingTerms are the terms of GetValueUnfinalized's `if (req.HasThing)` block
// and, for a pawn, its surrounding offsets and factors; they run for a thing
// request only.
func (e *Evaluator) pawnOffsetTerms(req *Request, p *PawnState, val float32) (float32, error) {
	stat := req.Stat
	name := stat.GetDefName()
	skills, err := need(p.Base.Skills, "the pawn's skills (pawn.skills)")
	if err != nil {
		return 0, err
	}
	if skills.Present {
		for _, n := range stat.GetSkillNeedOffsets() {
			v, err := skillNeedValue(n, skills)
			if err != nil {
				return 0, err
			}
			val = float32(val + v)
		}
	} else {
		val = float32(val + stat.GetNoSkillOffset())
	}
	if len(stat.GetCapacityOffsets()) > 0 {
		caps, err := need(p.Base.Capacities, "the pawn's capacity levels")
		if err != nil {
			return 0, err
		}
		for _, opt := range stat.GetCapacityOffsets() {
			co := opt.GetValue()
			level, ok := caps[co.GetCapacity()]
			if !ok {
				return 0, fmt.Errorf("stat evaluation needs the pawn's %s capacity level: not observed", co.GetCapacity())
			}
			// PawnCapacityOffset.GetOffset: (Mathf.Min(level, max) - 1) * scale.
			lv := level
			if co.GetMax() < lv {
				lv = co.GetMax()
			}
			val = float32(val + float32(float32(lv-1)*co.GetScale()))
		}
	}
	story, err := need(p.Base.Story, "the pawn's story (pawn.story)")
	if err != nil {
		return 0, err
	}
	if story.Present {
		for _, t := range story.Traits {
			if t.Suppressed {
				continue
			}
			v, err := e.traitStat(t, name, false)
			if err != nil {
				return 0, err
			}
			val = float32(val + v)
		}
	}
	hediffs, err := need(p.Body.Hediffs, "the pawn's hediffs")
	if err != nil {
		return 0, err
	}
	for _, h := range hediffs {
		stage, err := e.hediffStage(h)
		if err != nil {
			return 0, err
		}
		if stage == nil {
			continue
		}
		v, err := req.hediffOffset(stage, h.Severity)
		if err != nil {
			return 0, err
		}
		val = float32(val + v)
	}
	ideo, err := need(p.Body.Ideo, "the pawn's ideoligion (pawn.Ideo)")
	if err != nil {
		return 0, err
	}
	if ideo.Present {
		for _, pname := range ideo.Precepts {
			def, err := e.precept(pname)
			if err != nil {
				return 0, err
			}
			if len(def.GetStatOffsets()) > 0 {
				v, err := modifierFromList(def.GetStatOffsets(), name, 0)
				if err != nil {
					return 0, err
				}
				val = float32(val + v)
			}
			for _, aff := range def.GetConditionalStatAffecters() {
				c, err := affecterOf(aff)
				if err != nil {
					return 0, err
				}
				if len(c.offsets) == 0 {
					continue
				}
				applies, err := e.affecterApplies(req, c.class)
				if err != nil {
					return 0, err
				}
				if !applies {
					continue
				}
				v, err := modifierFromList(c.offsets, name, 0)
				if err != nil {
					return 0, err
				}
				val = float32(val + v)
			}
		}
		if ideo.Role != "" {
			def, err := e.precept(ideo.Role)
			if err != nil {
				return 0, err
			}
			for _, re := range def.GetRoleEffects() {
				if o := re.GetValue().GetRoleEffect_PawnStatOffset(); o != nil && o.GetStatDef() == name {
					val = float32(val + o.GetModifier())
				}
			}
		}
	}
	genes, err := e.activeGenes(p)
	if err != nil {
		return 0, err
	}
	for _, gname := range genes {
		def := bridge.DefRow[*d.GeneDef](e.catalog, gname)
		if def == nil {
			return 0, fmt.Errorf("catalog has no gene def %s", gname)
		}
		v, err := modifierFromList(def.GetStatOffsets(), name, 0)
		if err != nil {
			return 0, err
		}
		val = float32(val + v)
		for _, aff := range def.GetConditionalStatAffecters() {
			c, err := affecterOf(aff)
			if err != nil {
				return 0, err
			}
			applies, err := e.affecterApplies(req, c.class)
			if err != nil {
				return 0, err
			}
			if !applies {
				continue
			}
			v, err := modifierFromList(c.offsets, name, 0)
			if err != nil {
				return 0, err
			}
			val = float32(val + v)
		}
	}
	lifeStage, err := e.lifeStage(p)
	if err != nil {
		return 0, err
	}
	v, err := modifierFromList(lifeStage.GetStatOffsets(), name, 0)
	if err != nil {
		return 0, err
	}
	val = float32(val + v)
	apparel, err := need(p.Base.Apparel, "the pawn's worn apparel (pawn.apparel)")
	if err != nil {
		return 0, err
	}
	if apparel.Present {
		for _, g := range apparel.Worn {
			v, err := e.gearOffset(req, g)
			if err != nil {
				return 0, err
			}
			val = float32(val + v)
		}
	}
	primary, err := need(p.Base.Primary, "the pawn's primary weapon (pawn.equipment.Primary)")
	if err != nil {
		return 0, err
	}
	if primary != nil {
		v, err := e.gearOffset(req, *primary)
		if err != nil {
			return 0, err
		}
		val = float32(val + v)
	}
	return val, nil
}

// pawnFactorTerms are the pawn multipliers of GetValueUnfinalized, after the
// offsets.
func (e *Evaluator) pawnFactorTerms(req *Request, p *PawnState, val float32) (float32, error) {
	name := req.Stat.GetDefName()
	story, _ := need(p.Base.Story, "")
	if story.Present {
		for _, t := range story.Traits {
			if t.Suppressed {
				continue
			}
			v, err := e.traitStat(t, name, true)
			if err != nil {
				return 0, err
			}
			val = float32(val * v)
		}
	}
	hediffs, _ := need(p.Body.Hediffs, "")
	for _, h := range hediffs {
		stage, err := e.hediffStage(h)
		if err != nil {
			return 0, err
		}
		if stage == nil {
			continue
		}
		v, err := req.hediffFactor(stage, h.Severity)
		if err != nil {
			return 0, err
		}
		val = float32(val * v)
	}
	ideo, _ := need(p.Body.Ideo, "")
	if ideo.Present {
		for _, pname := range ideo.Precepts {
			def, err := e.precept(pname)
			if err != nil {
				return 0, err
			}
			if len(def.GetStatFactors()) > 0 {
				v, err := modifierFromList(def.GetStatFactors(), name, 1)
				if err != nil {
					return 0, err
				}
				val = float32(val * v)
			}
			for _, aff := range def.GetConditionalStatAffecters() {
				c, err := affecterOf(aff)
				if err != nil {
					return 0, err
				}
				if len(c.factors) == 0 {
					continue
				}
				applies, err := e.affecterApplies(req, c.class)
				if err != nil {
					return 0, err
				}
				if !applies {
					continue
				}
				v, err := modifierFromList(c.factors, name, 1)
				if err != nil {
					return 0, err
				}
				val = float32(val * v)
			}
		}
		if ideo.Role != "" {
			def, err := e.precept(ideo.Role)
			if err != nil {
				return 0, err
			}
			for _, re := range def.GetRoleEffects() {
				if f := re.GetValue().GetRoleEffect_PawnStatFactor(); f != nil && f.GetStatDef() == name {
					val = float32(val * f.GetModifier())
				}
			}
		}
	}
	genes, err := e.activeGenes(p)
	if err != nil {
		return 0, err
	}
	for _, gname := range genes {
		def := bridge.DefRow[*d.GeneDef](e.catalog, gname)
		v, err := modifierFromList(def.GetStatFactors(), name, 1)
		if err != nil {
			return 0, err
		}
		val = float32(val * v)
		for _, aff := range def.GetConditionalStatAffecters() {
			c, err := affecterOf(aff)
			if err != nil {
				return 0, err
			}
			applies, err := e.affecterApplies(req, c.class)
			if err != nil {
				return 0, err
			}
			if !applies {
				continue
			}
			v, err := modifierFromList(c.factors, name, 1)
			if err != nil {
				return 0, err
			}
			val = float32(val * v)
		}
	}
	lifeStage, err := e.lifeStage(p)
	if err != nil {
		return 0, err
	}
	v, err := modifierFromList(lifeStage.GetStatFactors(), name, 1)
	if err != nil {
		return 0, err
	}
	return float32(val * v), nil
}

// pawnThingTerms are the pawn part of the `req.HasThing` block: skill and
// capacity factors and the inspiration.
func (e *Evaluator) pawnThingTerms(req *Request, p *PawnState, val float32) (float32, error) {
	stat := req.Stat
	skills, err := need(p.Base.Skills, "the pawn's skills (pawn.skills)")
	if err != nil {
		return 0, err
	}
	if skills.Present {
		for _, n := range stat.GetSkillNeedFactors() {
			v, err := skillNeedValue(n, skills)
			if err != nil {
				return 0, err
			}
			val = float32(val * v)
		}
	} else {
		val = float32(val * stat.GetNoSkillFactor())
	}
	if len(stat.GetCapacityFactors()) > 0 {
		caps, err := need(p.Base.Capacities, "the pawn's capacity levels")
		if err != nil {
			return 0, err
		}
		for _, opt := range stat.GetCapacityFactors() {
			cf := opt.GetValue()
			level, ok := caps[cf.GetCapacity()]
			if !ok {
				return 0, fmt.Errorf("stat evaluation needs the pawn's %s capacity level: not observed", cf.GetCapacity())
			}
			factor := capacityFactor(cf, level)
			val = lerp32(val, float32(val*factor), cf.GetWeight())
		}
	}
	inspiration, err := need(p.Base.Inspiration, "the pawn's inspiration (pawn.Inspired)")
	if err != nil {
		return 0, err
	}
	if inspiration != "" {
		def := bridge.DefRow[*d.InspirationDef](e.catalog, inspiration)
		if def == nil {
			return 0, fmt.Errorf("catalog has no inspiration def %s", inspiration)
		}
		off, err := modifierFromList(def.GetStatOffsets(), stat.GetDefName(), 0)
		if err != nil {
			return 0, err
		}
		val = float32(val + off)
		fac, err := modifierFromList(def.GetStatFactors(), stat.GetDefName(), 1)
		if err != nil {
			return 0, err
		}
		val = float32(val * fac)
	}
	return val, nil
}

// capacityFactor is PawnCapacityFactor.GetFactor.
func capacityFactor(cf *d.PawnCapacityFactor, level float32) float32 {
	num := level
	if cf.GetAllowedDefect() != 0 && num < 1 {
		// Mathf.InverseLerp(0, 1 - allowedDefect, num), clamped to 0..1.
		b := float32(1 - cf.GetAllowedDefect())
		if b != 0 {
			num = float32(num-0) / float32(b-0)
			switch {
			case num < 0:
				num = 0
			case num > 1:
				num = 1
			}
		} else {
			num = 0
		}
	}
	if num > cf.GetMax() {
		num = cf.GetMax()
	}
	if cf.GetUseReciprocal() {
		if abs32(num) < 0.001 {
			num = 5
		} else {
			num = float32(1 / num)
			if num > 5 {
				num = 5
			}
		}
	}
	return num
}

// skillNeedValue is SkillNeed.ValueFor for a pawn with skills.
func skillNeedValue(opt *d.Opt_SkillNeedAny, skills SkillsState) (float32, error) {
	any := opt.GetValue()
	level := func(skill string) (int32, error) {
		l, ok := skills.Levels[skill]
		if !ok {
			return 0, fmt.Errorf("stat evaluation needs the pawn's %s skill level: not observed", skill)
		}
		return l, nil
	}
	switch {
	case any.GetSkillNeed_Direct() != nil:
		n := any.GetSkillNeed_Direct()
		l, err := level(n.GetSkill())
		if err != nil {
			return 0, err
		}
		values := n.GetValuesPerLevel()
		switch {
		case int32(len(values)) > l:
			return values[l], nil
		case len(values) > 0:
			return values[len(values)-1], nil
		}
		return 1, nil
	case any.GetSkillNeed_BaseBonus() != nil:
		n := any.GetSkillNeed_BaseBonus()
		l, err := level(n.GetSkill())
		if err != nil {
			return 0, err
		}
		return float32(n.GetBaseValue() + float32(n.GetBonusPerLevel()*float32(l))), nil
	case any.GetSkillNeed_Curve() != nil:
		n := any.GetSkillNeed_Curve()
		l, err := level(n.GetSkill())
		if err != nil {
			return 0, err
		}
		return EvaluateCurve(n.GetCurve(), float32(l))
	}
	return 0, fmt.Errorf("skill need entry has no concrete class (the base SkillNeed throws)")
}

// traitStat is Trait.OffsetOfStat (factor false) or MultiplierOfStat (factor
// true) for an unsuppressed trait: the sum or product of every matching entry
// of the degree's data.
func (e *Evaluator) traitStat(t TraitState, stat string, factor bool) (float32, error) {
	def := bridge.DefRow[*d.TraitDef](e.catalog, t.Def)
	if def == nil {
		return 0, fmt.Errorf("catalog has no trait def %s", t.Def)
	}
	datas := def.GetDegreeDatas()
	if len(datas) == 0 {
		return 0, fmt.Errorf("trait %s has no degree datas", t.Def)
	}
	data := datas[0].GetValue()
	for _, dd := range datas {
		if dd.GetValue().GetDegree() == t.Degree {
			data = dd.GetValue()
			break
		}
	}
	list, num := data.GetStatOffsets(), float32(0)
	if factor {
		list, num = data.GetStatFactors(), 1
	}
	for _, m := range list {
		mod := m.GetValue()
		if mod == nil {
			return 0, fmt.Errorf("empty stat modifier entry")
		}
		if mod.GetStat() != stat {
			continue
		}
		if factor {
			num = float32(num * mod.GetValue())
		} else {
			num = float32(num + mod.GetValue())
		}
	}
	return num, nil
}

// hediffStage is Hediff.CurStage: nil for no stage.
func (e *Evaluator) hediffStage(h HediffState) (*d.HediffStage, error) {
	switch h.Stage.Kind {
	case StageNone:
		return nil, nil
	case StageSynthetic:
		return &d.HediffStage{StatOffsets: statMods(h.Stage.StatOffsets), StatFactors: statMods(h.Stage.StatFactors)}, nil
	case StageDef:
		def := bridge.DefRow[*d.HediffDef](e.catalog, h.Def)
		if def == nil {
			return nil, fmt.Errorf("catalog has no hediff def %s", h.Def)
		}
		stages := def.GetStages()
		if h.Stage.Index < 0 || int(h.Stage.Index) >= len(stages) {
			return nil, fmt.Errorf("hediff %s has no stage %d", h.Def, h.Stage.Index)
		}
		return stages[h.Stage.Index].GetValue(), nil
	}
	return nil, fmt.Errorf("hediff %s has an unknown stage kind %d", h.Def, h.Stage.Kind)
}

// hediffOffset is HediffStatsUtility.GetStatOffsetForSeverity.
func (r *Request) hediffOffset(stage *d.HediffStage, severity float32) (float32, error) {
	name := r.Stat.GetDefName()
	num, err := modifierFromList(stage.GetStatOffsets(), name, 0)
	if err != nil {
		return 0, err
	}
	if num != 0 {
		if m := stage.GetStatOffsetEffectMultiplier(); m != "" {
			v, err := r.statValue(m)
			if err != nil {
				return 0, err
			}
			num = float32(num * v)
		}
		if stage.GetMultiplyStatChangesBySeverity() {
			num = float32(num * severity)
		}
		return num, nil
	}
	for _, m := range stage.GetStatOffsetsBySeverity() {
		if m.GetValue().GetStat() == name {
			return EvaluateCurve(m.GetValue().GetValueBySeverity(), severity)
		}
	}
	return 0, nil
}

// hediffFactor is HediffStatsUtility.GetStatFactorForSeverity.
func (r *Request) hediffFactor(stage *d.HediffStage, severity float32) (float32, error) {
	name := r.Stat.GetDefName()
	num, err := modifierFromList(stage.GetStatFactors(), name, 1)
	if err != nil {
		return 0, err
	}
	if abs32(float32(num-1)) > floatEpsilon {
		if m := stage.GetStatFactorEffectMultiplier(); m != "" {
			v, err := r.statValue(m)
			if err != nil {
				return 0, err
			}
			num = scaleFactor(num, v)
		}
		if stage.GetMultiplyStatChangesBySeverity() {
			num = scaleFactor(num, severity)
		}
		return num, nil
	}
	for _, m := range stage.GetStatFactorsBySeverity() {
		if m.GetValue().GetStat() == name {
			return EvaluateCurve(m.GetValue().GetValueBySeverity(), severity)
		}
	}
	return 1, nil
}

// scaleFactor is StatWorker.ScaleFactor.
func scaleFactor(factor, scale float32) float32 {
	return float32(1 - float32(float32(1-factor)*scale))
}

func (e *Evaluator) precept(name string) (*d.PreceptDef, error) {
	def := bridge.DefRow[*d.PreceptDef](e.catalog, name)
	if def == nil {
		return nil, fmt.Errorf("catalog has no precept def %s", name)
	}
	return def, nil
}

// activeGenes are the active genes' defs when Biotech is active and the pawn
// has a gene tracker; none otherwise (and then no fact is read).
func (e *Evaluator) activeGenes(p *PawnState) ([]string, error) {
	biotech, err := e.modActive(modBiotech)
	if err != nil || !biotech {
		return nil, err
	}
	genes, err := need(p.Body.Genes, "the pawn's genes (pawn.genes)")
	if err != nil || !genes.Present {
		return nil, err
	}
	var active []string
	for _, g := range genes.Genes {
		if !g.Overridden {
			active = append(active, g.Def)
		}
	}
	return active, nil
}

func (e *Evaluator) lifeStage(p *PawnState) (*d.LifeStageDef, error) {
	name, err := need(p.Body.CurLifeStage, "the pawn's life stage (ageTracker.CurLifeStage)")
	if err != nil {
		return nil, err
	}
	def := bridge.DefRow[*d.LifeStageDef](e.catalog, name)
	if def == nil {
		return nil, fmt.Errorf("catalog has no life stage def %s", name)
	}
	return def, nil
}

// --- ConditionalStatAffecter ---

type affecter struct {
	class            string
	offsets, factors []*d.Opt_StatModifier
}

// affecterOf is the concrete class of a conditional stat affecter and its
// offsets and factors.
func affecterOf(opt *d.Opt_ConditionalStatAffecterAny) (affecter, error) {
	a := opt.GetValue()
	switch {
	case a.GetConditionalStatAffecter_Child() != nil:
		c := a.GetConditionalStatAffecter_Child()
		return affecter{"Child", c.GetStatOffsets(), c.GetStatFactors()}, nil
	case a.GetConditionalStatAffecter_Clothed() != nil:
		c := a.GetConditionalStatAffecter_Clothed()
		return affecter{"Clothed", c.GetStatOffsets(), c.GetStatFactors()}, nil
	case a.GetConditionalStatAffecter_Unclothed() != nil:
		c := a.GetConditionalStatAffecter_Unclothed()
		return affecter{"Unclothed", c.GetStatOffsets(), c.GetStatFactors()}, nil
	case a.GetConditionalStatAffecter_InSpace() != nil:
		c := a.GetConditionalStatAffecter_InSpace()
		return affecter{"InSpace", c.GetStatOffsets(), c.GetStatFactors()}, nil
	case a.GetConditionalStatAffecter_NotInSpace() != nil:
		c := a.GetConditionalStatAffecter_NotInSpace()
		return affecter{"NotInSpace", c.GetStatOffsets(), c.GetStatFactors()}, nil
	case a.GetConditionalStatAffecter_InSunlight() != nil:
		c := a.GetConditionalStatAffecter_InSunlight()
		return affecter{"InSunlight", c.GetStatOffsets(), c.GetStatFactors()}, nil
	}
	return affecter{}, &bridge.NotMirrored{Class: "ConditionalStatAffecter", Fact: "a conditional stat affecter of a class with no Go port"}
}

// affecterApplies is ConditionalStatAffecter.Applies(req) of a concrete
// class (named as affecterOf returns it).
func (e *Evaluator) affecterApplies(req *Request, class string) (bool, error) {
	c := req.ctx()
	switch class {
	case "Child":
		biotech, err := e.modActive(modBiotech)
		if err != nil || !biotech {
			return false, err
		}
		p := req.pawn()
		if p == nil {
			return false, nil
		}
		human, err := humanlikeDef(req)
		if err != nil || !human {
			return false, err
		}
		stage, err := need(p.Base.Developmental, "the pawn's developmental stage")
		return stage&devChild != 0, err
	case "Clothed", "Unclothed":
		biotech, err := e.modActive(modBiotech)
		if err != nil {
			return false, err
		}
		if !biotech {
			return false, nil
		}
		clothed := false
		if p := req.pawn(); p != nil {
			apparel, err := need(p.Base.Apparel, "the pawn's worn apparel (pawn.apparel)")
			if err != nil {
				return false, err
			}
			if apparel.Present {
				for _, g := range apparel.Worn {
					def := e.catalog.ThingDef(g.Subject.Def)
					if def == nil {
						return false, fmt.Errorf("catalog has no thing def %s", g.Subject.Def)
					}
					if def.GetApparel().GetCountsAsClothingForNudity() {
						clothed = true
						break
					}
				}
			}
		}
		if class == "Clothed" {
			return clothed, nil
		}
		return !clothed, nil
	case "InSpace", "NotInSpace":
		inSpace := false
		if c != nil {
			parent, err := need(c.Base.SpawnedOrParentSpawned, "whether the thing or a parent is spawned")
			if err != nil {
				return false, err
			}
			if parent {
				if inSpace, err = need(c.Base.InSpaceLayer, "whether the thing's map tile is in a space layer"); err != nil {
					return false, err
				}
			}
		}
		if class == "NotInSpace" {
			return !inSpace, nil
		}
		return inSpace, nil
	case "InSunlight":
		biotech, err := e.modActive(modBiotech)
		if err != nil || !biotech || c == nil {
			return false, err
		}
		spawned, err := need(c.Spawned, "whether the thing is spawned")
		if err != nil || !spawned {
			return false, err
		}
		return need(c.Base.InSunlight, "whether the thing's cell is in sunlight")
	}
	return false, fmt.Errorf("unknown conditional stat affecter %s", class)
}

// devChild is DevelopmentalStage.Child.
const devChild int32 = 4

// --- comps ---

// compClasses are the thing def's comp classes in order (CompProperties.compClass).
func compClasses(t *d.ThingDef) ([]string, error) {
	var out []string
	for i, opt := range t.GetComps() {
		comp := opt.GetValue()
		if comp == nil {
			return nil, fmt.Errorf("thing def %s has an empty comp at index %d", t.GetDefName(), i)
		}
		msg := comp.ProtoReflect()
		field := msg.WhichOneof(msg.Descriptor().Oneofs().ByName("value"))
		if field == nil {
			return nil, fmt.Errorf("thing def %s comp %d names no class", t.GetDefName(), i)
		}
		inner := msg.Get(field).Message()
		out = append(out, inner.Get(inner.Descriptor().Fields().ByName("compClass")).String())
	}
	return out, nil
}

// compStat is the thing's comps' GetStatOffset (factor false) or
// GetStatFactor (factor true), applied to val in comp order. The only
// ThingComp classes of the game that override them are CompUniqueWeapon and
// CompAffectedByFacilities (offset only); each is a typed fact, and every
// other comp contributes the base 0 / 1.
func (e *Evaluator) compStat(req *Request, val float32, factor bool) (float32, error) {
	c := req.ctx()
	classes, err := compClasses(req.Thing)
	if err != nil {
		return 0, err
	}
	stat := req.Stat.GetDefName()
	for _, class := range classes {
		unique, err := e.classIsA(class, classCompUniqueWeapon)
		if err != nil {
			return 0, err
		}
		if unique {
			traits, err := need(c.Base.UniqueWeaponTraits, "the weapon traits of the thing's CompUniqueWeapon")
			if err != nil {
				return 0, err
			}
			sum := float32(0)
			if factor {
				sum = 1
			}
			for _, tname := range traits {
				def := bridge.DefRow[*d.WeaponTraitDef](e.catalog, tname)
				if def == nil {
					return 0, fmt.Errorf("catalog has no weapon trait def %s", tname)
				}
				if factor {
					v, err := modifierFromList(def.GetStatFactors(), stat, 1)
					if err != nil {
						return 0, err
					}
					sum = float32(sum * v)
				} else {
					v, err := modifierFromList(def.GetStatOffsets(), stat, 0)
					if err != nil {
						return 0, err
					}
					sum = float32(sum + v)
				}
			}
			if factor {
				val = float32(val * sum)
			} else {
				val = float32(val + sum)
			}
			continue
		}
		if factor {
			continue
		}
		faci, err := e.classIsA(class, classCompFacilities)
		if err != nil {
			return 0, err
		}
		if !faci {
			continue
		}
		links, err := need(c.Base.Facilities, "the linked facilities of the thing's CompAffectedByFacilities")
		if err != nil {
			return 0, err
		}
		sum := float32(0)
		for _, link := range links {
			if link.StatOffsets == nil {
				continue
			}
			v, err := modifierFromList(statMods(link.StatOffsets), stat, 0)
			if err != nil {
				return 0, err
			}
			if v != 0 && link.Active {
				sum = float32(sum + v)
			}
		}
		val = float32(val + sum)
	}
	return val, nil
}

// thingTerms is the `if (req.HasThing)` block of GetValueUnfinalized.
func (e *Evaluator) thingTerms(req *Request, val float32) (float32, error) {
	stat := req.Stat
	var err error
	if val, err = e.compStat(req, val, false); err != nil {
		return 0, err
	}
	if val, err = e.compStat(req, val, true); err != nil {
		return 0, err
	}
	for _, other := range stat.GetStatFactors() {
		v, err := req.statValue(other)
		if err != nil {
			return 0, err
		}
		val = float32(val * v)
	}
	if p := req.pawn(); p != nil {
		return e.pawnThingTerms(req, p, val)
	}
	return val, nil
}

// gearOffset is StatWorker.StatOffsetFromGear: the gear def's equipped
// offsets and its bladelink traits', run through the stat's parts as a request
// for the gear itself.
func (e *Evaluator) gearOffset(req *Request, g GearPiece) (float32, error) {
	stat := req.Stat
	name := stat.GetDefName()
	def := e.catalog.ThingDef(g.Subject.Def)
	if def == nil {
		return 0, fmt.Errorf("catalog has no thing def %s", g.Subject.Def)
	}
	val, err := modifierFromList(def.GetEquippedStatOffsets(), name, 0)
	if err != nil {
		return 0, err
	}
	blade, err := e.hasCompFrom(def, classCompBladelink)
	if err != nil {
		return 0, err
	}
	if blade {
		traits, err := need(g.BladelinkTraits, fmt.Sprintf("the bladelink traits of %s", g.Subject.Def))
		if err != nil {
			return 0, err
		}
		for _, tname := range traits {
			trait := bridge.DefRow[*d.WeaponTraitDef](e.catalog, tname)
			if trait == nil {
				return 0, fmt.Errorf("catalog has no weapon trait def %s", tname)
			}
			v, err := modifierFromList(trait.GetEquippedStatOffsets(), name, 0)
			if err != nil {
				return 0, err
			}
			val = float32(val + v)
		}
	}
	if abs32(val) > floatEpsilon && len(stat.GetParts()) > 0 {
		if g.Subject.Context == nil {
			return 0, fmt.Errorf("stat evaluation needs the thing context of %s: not observed", g.Subject.Def)
		}
		gearReq, err := e.request(name, g.Subject)
		if err != nil {
			return 0, err
		}
		parts, err := e.orderedParts(stat)
		if err != nil {
			return 0, err
		}
		for _, rp := range parts {
			part, err := e.part(rp.class, "TransformValue")
			if err != nil {
				return 0, err
			}
			if val, err = part.Transform(gearReq, rp.row, val); err != nil {
				return 0, err
			}
		}
	}
	return val, nil
}
