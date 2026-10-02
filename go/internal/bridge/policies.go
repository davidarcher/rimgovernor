package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

// validatePolicies checks the colony's policy databases (#1297): each
// database has unique ids, at most one default, and a pawn is the current
// holder of at most one policy per database and one allowed area.
func validatePolicies(v *o.ColonyFactsSnapshot) error {
	if v.Policies == nil {
		return nil
	}
	switch s := v.Policies.Outcome.(type) {
	case *o.PolicySection_Unavailable:
		return validateUnavailable(s.Unavailable)
	case *o.PolicySection_Observed:
		f := s.Observed
		if f == nil {
			return contract("incomplete policy facts")
		}
		for i, rows := range [][]*o.PolicyEntry{f.Outfit, f.Drug, f.Food, f.Reading} {
			ids, pawns, defaults := map[string]bool{}, map[string]bool{}, 0
			for _, row := range rows {
				if row == nil || validID(row.GetId()) != nil || ids[row.GetId()] || row.Label == nil || !diagnostic(row.Label) || row.Default == nil {
					return contract("invalid policy entry")
				}
				ids[row.GetId()] = true
				if i != 3 && len(row.AllowedDefs) > 0 {
					return contract("allowed definitions on a non-reading policy")
				}
				for _, d := range row.AllowedDefs {
					if validID(d) != nil {
						return contract("invalid reading policy definition")
					}
				}
				if i != 1 && len(row.DrugEntries) > 0 {
					return contract("drug entries on a non-drug policy")
				}
				drugs := map[string]bool{}
				for _, e := range row.DrugEntries {
					if e == nil || validID(e.GetDrugDef()) != nil || drugs[e.GetDrugDef()] || e.AllowedForJoy == nil || e.AllowedForAddiction == nil || e.AllowScheduled == nil || e.DaysFrequency == nil || e.OnlyIfMoodBelow == nil || e.OnlyIfJoyBelow == nil || e.TakeToInventory == nil {
						return contract("invalid drug policy entry")
					}
					drugs[e.GetDrugDef()] = true
				}
				if row.GetDefault() {
					defaults++
				}
				if err := uniquePawns(row.PawnIds, pawns); err != nil {
					return err
				}
			}
			if defaults > 1 {
				return contract("several default policies")
			}
		}
		books := map[string]bool{}
		for _, b := range f.Books {
			if b == nil || validID(b.GetDefName()) != nil || books[b.GetDefName()] || b.GetKind() == o.BookKind_BOOK_KIND_UNSPECIFIED || o.BookKind_name[int32(b.GetKind())] == "" {
				return contract("invalid book definition")
			}
			books[b.GetDefName()] = true
		}
		for _, d := range f.BiomeDiseases {
			if validID(d) != nil {
				return contract("invalid biome disease")
			}
		}
		ids, pawns := map[string]bool{}, map[string]bool{}
		for _, row := range f.AllowedAreas {
			if row == nil || validID(row.GetId()) != nil || ids[row.GetId()] || row.Label == nil || !diagnostic(row.Label) {
				return contract("invalid allowed area")
			}
			ids[row.GetId()] = true
			if err := uniquePawns(row.PawnIds, pawns); err != nil {
				return err
			}
		}
		return nil
	default:
		return contract("policy section outcome")
	}
}

func uniquePawns(ids []string, seen map[string]bool) error {
	for _, id := range ids {
		if validID(id) != nil || seen[id] {
			return contract("invalid or doubly assigned policy pawn")
		}
		seen[id] = true
	}
	return nil
}

// validatePolicyInputs checks the per-pawn policy inputs row (#1297).
func validatePolicyInputs(p *o.PawnPolicyInputs) error {
	if p == nil {
		return nil
	}
	for _, id := range []*string{p.OutfitPolicyId, p.DrugPolicyId, p.ReadingPolicyId, p.RoyalTitle, p.IdeoId, p.IdeoRole, p.GuestStatus, p.PrisonerInteraction, p.SlaveInteraction} {
		if id != nil && validID(*id) != nil {
			return contract("invalid policy input identifier")
		}
	}
	if !combatNumber(p.MedicalTendQuality, true) {
		return contract("invalid medical tend quality")
	}
	groups := map[string]bool{}
	for _, s := range p.InventoryStock {
		if s == nil || validID(s.GetGroup()) != nil || groups[s.GetGroup()] || validID(s.GetThingDef()) != nil || s.Count == nil || s.GetCount() < 0 {
			return contract("invalid inventory stock setting")
		}
		groups[s.GetGroup()] = true
	}
	chemicals := map[string]bool{}
	for _, c := range p.Chemicals {
		if c == nil || validID(c.GetChemical()) != nil || chemicals[c.GetChemical()] || c.Addiction == nil && c.Tolerance == nil || (c.Addiction == nil) != (c.Withdrawal == nil) || !combatNumber(c.Addiction, true) || !combatNumber(c.Tolerance, true) {
			return contract("invalid chemical state")
		}
		chemicals[c.GetChemical()] = true
	}
	for _, list := range [][]string{p.DependencyChemicals, p.Precepts, p.PreceptApparel} {
		for _, id := range list {
			if validID(id) != nil {
				return contract("invalid policy input definition")
			}
		}
	}
	for _, r := range append(append([]*o.ApparelRequirementFact(nil), p.TitleApparel...), p.RoleApparel...) {
		if r == nil {
			return contract("invalid apparel requirement")
		}
		for _, list := range [][]string{r.BodyPartGroups, r.RequiredDefs, r.RequiredTags, r.AllowedTags} {
			for _, id := range list {
				if validID(id) != nil {
					return contract("invalid apparel requirement")
				}
			}
		}
	}
	return nil
}
