package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func removeAddedOp(body string, index int, hediff, item string, value float64) SurgeryOperation {
	return SurgeryOperation{Recipe: domain.Known("RemoveBodyPart"), PartDefName: domain.Known(body), PartIndex: domain.Known(index), Kind: SurgeryAmputate,
		SuccessChance: domain.Known(0.95), EligibleDoctors: domain.Known(1), IngredientsOnMap: domain.Known(true),
		Violation: domain.Known(false), Lethal: domain.Known(false), AddedPart: domain.Known(hediff), YieldThing: domain.Known(item),
		YieldValue: domain.Known(value), MedicineValue: domain.Known(18.0)}
}

func recoveryPrisoner(id string, missing []string, ops ...SurgeryOperation) PrisonerFacts {
	row := harvestPrisoner(id, -70, ops...)
	parts := []MissingPart{}
	for _, name := range missing {
		parts = append(parts, MissingPart{PartDefName: domain.Known(name)})
	}
	row.MissingParts = domain.Known(parts)
	return row
}

func TestSelectPartRecovery(t *testing.T) {
	arm := removeAddedOp("Shoulder", 10, "BionicArm", "BionicArm", 1400)
	missingArm := surgeryPawn("c", 0, restoreOp("InstallBionicArm", "Shoulder", 10, 0.9, 1, false))
	needs := PartRecoveryNeeds(domain.Known([]CarePawn{missingArm}), SelectSurgery(domain.Known([]CarePawn{missingArm}), nil, SurgeryContext{}).Wants, testRecipeFacts)
	if len(needs) != 1 || needs[0].Organ != "BionicArm" || needs[0].For != "c" || needs[0].Gain <= 0 {
		t.Fatalf("needs %+v", needs)
	}
	five := PrisonerColony{Colonists: 5, BestSkill: core.BestSkill}
	violating := arm
	violating.Violation = domain.Known(true)
	cheap := removeAddedOp("Shoulder", 10, "SimpleProstheticArm", "SimpleProstheticArm", 10)
	lethal := removeAddedOp("Torso", 3, "BionicStomach", "BionicStomach", 800)
	lethal.Lethal = domain.Known(true)
	for _, c := range []struct {
		name      string
		prisoners []PrisonerFacts
		needs     []OrganNeed
		inFlight  map[PawnID]bool
		want      string // "prisoner/part/for/violation", "" for none
	}{
		{"bionic arm for a colonist missing one", []PrisonerFacts{recoveryPrisoner("p", nil, arm)}, needs, nil, "p/10/c/false"},
		{"bionic arm to stock at market value", []PrisonerFacts{recoveryPrisoner("p", nil, arm)}, nil, nil, "p/10//false"},
		{"part worth less than the medicine stays", []PrisonerFacts{recoveryPrisoner("p", nil, cheap)}, nil, nil, ""},
		{"native violation adds mood and goodwill and is acknowledged", []PrisonerFacts{recoveryPrisoner("p", nil, violating)}, nil, nil, "p/10//true"},
		{"violation cost over a cheap part refuses", []PrisonerFacts{recoveryPrisoner("p", nil, func() SurgeryOperation {
			op := removeAddedOp("Shoulder", 10, "SimpleProstheticArm", "SimpleProstheticArm", 500)
			op.Violation = domain.Known(true)
			return op
		}())}, nil, nil, ""},
		{"peg leg gives only wood", []PrisonerFacts{recoveryPrisoner("p", nil, removeAddedOp("Leg", 30, "PegLeg", "WoodLog", 1.2))}, nil, nil, ""},
		{"wooden hand", []PrisonerFacts{recoveryPrisoner("p", nil, removeAddedOp("Hand", 12, "WoodenHand", "WoodLog", 1.2))}, nil, nil, ""},
		{"dentures", []PrisonerFacts{recoveryPrisoner("p", nil, removeAddedOp("Jaw", 5, "Denture", "Denture", 50))}, nil, nil, ""},
		{"bionic heart stays", []PrisonerFacts{recoveryPrisoner("p", nil, removeAddedOp("Heart", 16, "BionicHeart", "BionicHeart", 1800))}, nil, nil, ""},
		{"lethal removal refused", []PrisonerFacts{recoveryPrisoner("p", nil, lethal)}, nil, nil, ""},
		{"joywire is not removable", []PrisonerFacts{recoveryPrisoner("p", nil, removeAddedOp("Brain", 2, "Joywire", "Joywire", 800))}, nil, nil, ""},
		{"bionic spine stays", []PrisonerFacts{recoveryPrisoner("p", nil, removeAddedOp("Spine", 4, "BionicSpine", "BionicSpine", 1800))}, nil, nil, ""},
		{"second leg stays", []PrisonerFacts{recoveryPrisoner("p", []string{"Leg"}, removeAddedOp("Leg", 30, "BionicLeg", "BionicLeg", 1400))}, nil, nil, ""},
		{"first bionic leg recovered", []PrisonerFacts{recoveryPrisoner("p", nil, removeAddedOp("Leg", 30, "BionicLeg", "BionicLeg", 1400))}, nil, nil, "p/30//false"},
		{"natural part removal is not recovery", []PrisonerFacts{recoveryPrisoner("p", nil, harvestOp("Kidney", 20, false))}, nil, nil, ""},
		{"one surgery at a time", []PrisonerFacts{recoveryPrisoner("p", nil, arm)}, nil, map[PawnID]bool{"p": true}, ""},
		{"recruitable worthy prisoner kept whole", []PrisonerFacts{func() PrisonerFacts {
			row := recoveryPrisoner("p", nil, arm)
			row.Recruitable, row.Prospect = domain.Known(true), domain.Known(strong)
			return row
		}()}, nil, nil, ""},
		{"unknown missing parts refuse", []PrisonerFacts{func() PrisonerFacts {
			row := recoveryPrisoner("p", nil, arm)
			row.MissingParts = domain.Unknown[[]MissingPart]()
			return row
		}()}, nil, nil, ""},
	} {
		got, ok := SelectPartRecovery(domain.Known(c.prisoners), domain.Known(five), c.needs, c.inFlight, testRecipeFacts)
		key := ""
		if ok {
			key = fmt.Sprintf("%s/%d/%s/%v", got.Prisoner, got.Part, got.For, got.Violation)
		}
		if key != c.want {
			t.Fatalf("%s: got %q (%+v) want %q", c.name, key, got, c.want)
		}
	}
}
