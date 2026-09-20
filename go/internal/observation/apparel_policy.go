package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func ApparelPolicyFacts(p *o.GearLoadout) domain.Fact[policy.ApparelPolicyState] {
	v := p.GetApparelPolicy()
	if v == nil {
		return domain.Unknown[policy.ApparelPolicyState]()
	}
	s := policy.ApparelPolicyState{Token: v.GetToken(), ExcludesTainted: v.GetExcludesTainted(), Role: policy.GearRoleInput{Child: v.GetChild(), Slave: v.GetSlave(), IncapableOfViolence: v.GetIncapableOfViolence(), DraftedSquad: v.GetDrafted()}, Current: domain.ApparelPolicySpec{Name: v.GetName(), Definitions: append([]string{}, v.AllowedDefs...), MinHP: float64(v.GetMinHitPoints()), MaxHP: float64(v.GetMaxHitPoints()), MinQuality: v.GetMinQuality(), MaxQuality: v.GetMaxQuality()}}
	for _, d := range v.Definitions {
		s.Definitions = append(s.Definitions, policy.ApparelDefinition{Name: d.GetDefName(), Armor: d.GetArmor(), Child: d.GetChild(), Adult: d.GetAdult()})
	}
	for _, a := range p.GetEquipment().GetApparel() {
		s.Overrides = s.Overrides || a.GetForced() || a.GetLocked()
	}
	priorities := []policy.WorkPriority{}
	for _, w := range v.Work {
		priorities = append(priorities, policy.WorkPriority{Work: policy.WorkType(w.GetDefName()), Priority: int(w.GetPriority()), Disabled: w.GetDisabled()})
	}
	s.Role.Work.Work = domain.Known(priorities)
	return domain.Known(s)
}
