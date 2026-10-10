package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func optionalFact[T any](p *T) domain.Fact[T] {
	if p == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*p)
}

func apparelRequirements(rows []*o.ApparelRequirementFact) []policy.ApparelRequirement {
	r := make([]policy.ApparelRequirement, 0, len(rows))
	for _, row := range rows {
		r = append(r, policy.ApparelRequirement{BodyPartGroups: row.BodyPartGroups, RequiredDefs: row.RequiredDefs, RequiredTags: row.RequiredTags, AllowedTags: row.AllowedTags})
	}
	return r
}

// PawnPolicyInputs lifts a settings row's policy inputs; unknown when the
// producer carried none.
func PawnPolicyInputs(p *o.PawnPolicyInputs) domain.Fact[policy.PawnPolicyInputs] {
	if p == nil {
		return domain.Fact[policy.PawnPolicyInputs]{}
	}
	r := policy.PawnPolicyInputs{
		OutfitPolicy: p.GetOutfitPolicyId(), DrugPolicy: p.GetDrugPolicyId(), ReadingPolicy: p.GetReadingPolicyId(),
		DependencyChemicals: p.DependencyChemicals, RoyalTitle: p.GetRoyalTitle(),
		Ideo: p.GetIdeoId(), Precepts: p.Precepts, IdeoRole: p.GetIdeoRole(), IdeoCertainty: optionalFact(p.IdeoCertainty), RoleApparel: apparelRequirements(p.RoleApparel), PreceptApparel: p.PreceptApparel,
		GuestStatus: p.GetGuestStatus(), PrisonerInteraction: p.GetPrisonerInteraction(), SlaveInteraction: p.GetSlaveInteraction(),
		TendQuality: optionalFact(p.MedicalTendQuality),
	}
	for _, s := range p.InventoryStock {
		r.InventoryStock = append(r.InventoryStock, policy.InventoryStock{Group: s.GetGroup(), Thing: s.GetThingDef(), Count: int(s.GetCount())})
	}
	for _, c := range p.Chemicals {
		r.Chemicals = append(r.Chemicals, policy.ChemicalState{Chemical: c.GetChemical(), Addiction: optionalFact(c.Addiction), Withdrawal: c.GetWithdrawal(), Tolerance: optionalFact(c.Tolerance)})
	}
	return domain.Known(r)
}
