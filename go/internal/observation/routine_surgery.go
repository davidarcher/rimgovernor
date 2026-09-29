package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

var surgeryKinds = map[o.SurgeryKind]policy.SurgeryKind{
	o.SurgeryKind_SURGERY_KIND_RESTORE:  policy.SurgeryRestore,
	o.SurgeryKind_SURGERY_KIND_CURE:     policy.SurgeryCure,
	o.SurgeryKind_SURGERY_KIND_AMPUTATE: policy.SurgeryAmputate,
	o.SurgeryKind_SURGERY_KIND_INSTALL:  policy.SurgeryInstall,
	o.SurgeryKind_SURGERY_KIND_HARVEST:  policy.SurgeryHarvest,
	o.SurgeryKind_SURGERY_KIND_OTHER:    policy.SurgeryOther,
}

// surgeryFacts maps the native surgery facts (#1161) as observed. A list
// carrying a read issue is unknown; values are never recomputed here.
func surgeryFacts(h *o.PawnHealth) (domain.Fact[[]policy.MissingPart], domain.Fact[[]policy.SurgeryOperation]) {
	parts, ops := domain.Unknown[[]policy.MissingPart](), domain.Unknown[[]policy.SurgeryOperation]()
	if !hasIssue(h.Issues, "missing_parts") {
		rows := make([]policy.MissingPart, 0, len(h.MissingParts))
		for _, m := range h.MissingParts {
			if m != nil {
				rows = append(rows, policy.MissingPart{
					PartIndex: optionalInt(m.PartIndex), ParentIndex: optionalInt(m.ParentIndex),
					PartDefName: optional(m.PartDefName), ParentDefName: optional(m.ParentDefName), Vital: optional(m.Vital),
				})
			}
		}
		parts = domain.Known(rows)
	}
	if !hasIssue(h.Issues, "operations") {
		rows := make([]policy.SurgeryOperation, 0, len(h.Operations))
		for _, op := range h.Operations {
			if op == nil {
				continue
			}
			row := policy.SurgeryOperation{
				PartIndex: optionalInt(op.PartIndex), PartDefName: optional(op.PartDefName), Kind: surgeryKinds[op.GetKind()],
				SuccessChance: optional(op.SuccessChance), IngredientsOnMap: optional(op.IngredientsOnMap),
				Violation: optional(op.Violation), Lethal: optional(op.Lethal),
			}
			if op.Recipe != nil {
				row.Recipe = optional(op.Recipe.DefName)
			}
			if op.EligibleDoctors != nil {
				row.EligibleDoctors = domain.Known(int(op.GetEligibleDoctors()))
			}
			rows = append(rows, row)
		}
		ops = domain.Known(rows)
	}
	return parts, ops
}

func optionalInt(p *int32) domain.Fact[int] {
	if p == nil {
		return domain.Unknown[int]()
	}
	return domain.Known(int(*p))
}
