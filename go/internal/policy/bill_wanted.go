package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// BillWanted reports whether any product of a bill is in wanted: a finite bill
// matching none of an owner's current demand is stale whatever the owner's
// finding.
func BillWanted(products []Resource, wanted map[Resource]bool) bool {
	for _, p := range products {
		if wanted[p] {
			return true
		}
	}
	return false
}

// GearBillsWanted is the set of apparel and armor definitions a gear bill may
// still make: every replacement a pawn's loadout model names, blocked pawns
// included (a busy pawn still wants its garment), and for an armor definition
// every rung of its family. Stored stock is not netted, so the set only
// over-states what is wanted. known is false while the census or a pawn's
// replacements are unread.
func GearBillsWanted(gear domain.Fact[GearObservation]) (wanted map[Resource]bool, known bool, err error) {
	review, err := ReviewGear(gear)
	if err != nil {
		return nil, false, err
	}
	if _, ok := review.Recovered.Value(); !ok {
		return nil, false, nil
	}
	wanted = map[Resource]bool{}
	if positive(review.Recovered) {
		return wanted, true, nil
	}
	v, _ := gear.Value()
	for _, p := range modeledGearObservation(v, review.Loadouts).Pawns {
		needs, ok := p.Replacements.Value()
		if !ok {
			return nil, false, nil
		}
		for _, n := range needs {
			if rungs, armor := armoryArmorRungs(n.Definition, ArmoryTierFabrication); armor {
				for _, rung := range rungs {
					wanted[rung] = true
				}
				continue
			}
			wanted[n.Definition] = true
		}
	}
	return wanted, true, nil
}

// SurgeryPartsWanted is the set of items the part demand names: a part bill
// making none of them has no operation waiting on it.
func SurgeryPartsWanted(parts []SurgeryPart) map[Resource]bool {
	wanted := map[Resource]bool{}
	for _, part := range parts {
		for _, item := range part.Items {
			wanted[item] = true
		}
	}
	return wanted
}

// WeaponsWanted is the set of weapon definitions the armory demand names.
func WeaponsWanted(demand ...[]Amount) map[Resource]bool {
	wanted := map[Resource]bool{}
	for _, amounts := range demand {
		for _, a := range amounts {
			if a.Count > 0 {
				wanted[a.Resource] = true
			}
		}
	}
	return wanted
}
