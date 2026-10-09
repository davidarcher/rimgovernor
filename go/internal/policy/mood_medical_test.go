package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The audited pain and sickness ThoughtDefs: Pain (PainTotal from injuries and
// chronic conditions), Sick (any visible hediff with makesSickThought: the
// infections and diseases) and BabySick (a child's InfantIllness).
// MaintainMedicalReserves owns them: its care phase rests and beds the sick,
// tending spends the medicine it stocks, and the tended condition heals.
var medicalThoughts = []string{"Pain", "Sick", "BabySick"}

func TestMedicalThoughtsAreOwnedByMedicalReserves(t *testing.T) {
	for _, def := range medicalThoughts {
		if owners := thoughtOwners(def); len(owners) != 1 || owners[0] != MaintainMedicalReserves {
			t.Errorf("%s owners = %v", def, owners)
		}
	}
	// A masochist's pleasure and the precept's idealized pain are not
	// something medicine removes.
	for _, def := range []string{"MasochistPain", "Pain_Idealized"} {
		if len(thoughtOwners(def)) != 0 {
			t.Errorf("%s should stay unowned", def)
		}
	}
	l := BuildMoodLedger([]MoodLedgerPawn{ledgerPawn("a", MoodThought{"Pain", -10}, MoodThought{"Sick", -5}), ledgerPawn("b", MoodThought{"BabySick", -4})}, nil)
	if len(l.Sources) != 3 || len(l.Unowned) != 0 {
		t.Fatalf("medical thoughts unowned: %+v", l)
	}
	for _, s := range l.Sources {
		if len(s.Owners) != 1 || s.Owners[0] != MaintainMedicalReserves {
			t.Errorf("%s ledger owners = %v", s.Def, s.Owners)
		}
	}
}

// Dominant pain or sickness pressure raises the medical concern's deficit to
// the fraction of pawns under it; a pawn without those thoughts raises none.
func TestMedicalThoughtRaisesMedicalDeficit(t *testing.T) {
	for _, def := range medicalThoughts {
		p := moodPawn()
		p.Thoughts = domain.Known([]MoodThought{{def, -8}, {"Insulted", -1}})
		deficit, raised := MoodProvisionDeficits(moodReview(t, p, MoodHistory{}))[MaintainMedicalReserves]
		if !raised || deficit != 1 {
			t.Fatalf("%s deficit = %v, %v", def, deficit, raised)
		}
	}
	p := moodPawn()
	p.Thoughts = domain.Known([]MoodThought{{"Insulted", -8}})
	if _, raised := MoodProvisionDeficits(moodReview(t, p, MoodHistory{}))[MaintainMedicalReserves]; raised {
		t.Fatal("a pawn without a medical thought raised the medical deficit")
	}
}
