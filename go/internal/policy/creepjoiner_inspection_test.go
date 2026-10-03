package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var inspectionDefs = InspectionRecipes{"InspectX": true}

func inspectOp(recipe string, part int, doctors int, stocked bool) SurgeryOperation {
	op := SurgeryOperation{Recipe: domain.Known(recipe), PartIndex: domain.Known(part), Kind: SurgeryOther, EligibleDoctors: domain.Known(doctors), IngredientsOnMap: domain.Known(stocked)}
	return op
}

func inspectable(pawn PawnID, triggered bool, ops ...SurgeryOperation) CreepJoinerHand {
	return CreepJoinerHand{
		Pawn: pawn, Facts: hiddenJoiner(domain.Known(triggered), defNames("Kind"), defNames()),
		Available: domain.Known(true), Weapon: domain.Known(""),
		Operations: domain.Known(ops), QueuedSurgeries: domain.Known(0), QueuedRecipes: nil,
	}
}

// TestInspectionOrdersOnlyAnUnrevealedCreepJoiner (#1740): a creepjoiner whose
// downside is hidden is ordered the inspection the def mirror's recipe names,
// on its lowest part; a revealed one, a colonist that is none and one already
// in the record are not.
func TestInspectionOrdersOnlyAnUnrevealedCreepJoiner(t *testing.T) {
	plain := CreepJoinerHand{Pawn: "plain", Facts: CreepJoinerPawn{CreepJoiner: domain.Known[*CreepJoiner](nil), Traits: defNames(), Hediffs: defNames()}}
	ops := []SurgeryOperation{inspectOp("Other", 0, 1, true), inspectOp("InspectX", 7, 1, true), inspectOp("InspectX", 3, 1, true)}
	hands := []CreepJoinerHand{plain, inspectable("shown", true, ops...), inspectable("hidden", false, ops...), inspectable("done", false, ops...)}
	got := downsideDefs.Inspections(hands, inspectionDefs, CreepJoinerRecord{Inspections: map[PawnID]InspectionStage{"done": InspectionDone, "gone": InspectionDone}})
	if len(got.Orders) != 1 || got.Orders[0] != (InspectionOrder{Pawn: "hidden", Recipe: "InspectX", Part: 3}) {
		t.Fatalf("orders = %+v", got.Orders)
	}
	want := map[PawnID]InspectionStage{"done": InspectionDone, "hidden": InspectionOrdered}
	if len(got.Record.Inspections) != 2 || got.Record.Inspections["done"] != InspectionDone || got.Record.Inspections["hidden"] != InspectionOrdered {
		t.Fatalf("record = %+v, want %+v (a pawn that left drops out)", got.Record.Inspections, want)
	}
}

// TestInspectionEndsWhenTheBillIsGone: an ordered inspection is done once no
// inspection bill is queued; a queued one, or an unread bill stack, leaves it
// ordered.
func TestInspectionEndsWhenTheBillIsGone(t *testing.T) {
	ordered := CreepJoinerRecord{Inspections: map[PawnID]InspectionStage{"p": InspectionOrdered}}
	for _, tc := range []struct {
		name   string
		queued domain.Fact[int]
		recipe []string
		stage  InspectionStage
	}{
		{"no bills", domain.Known(0), nil, InspectionDone},
		{"inspection queued", domain.Known(1), []string{"InspectX"}, InspectionOrdered},
		{"another bill only", domain.Known(1), []string{"Other"}, InspectionDone},
		{"bills unread", domain.Unknown[int](), nil, InspectionOrdered},
		{"recipes unread", domain.Known(1), nil, InspectionOrdered},
	} {
		h := inspectable("p", false)
		h.QueuedSurgeries, h.QueuedRecipes = tc.queued, tc.recipe
		got := downsideDefs.Inspections([]CreepJoinerHand{h}, inspectionDefs, ordered)
		if got.Record.Inspections["p"] != tc.stage || len(got.Orders) != 0 {
			t.Errorf("%s: record %v orders %v, want %s", tc.name, got.Record.Inspections, got.Orders, tc.stage)
		}
		if owed := got.Owed(ordered); owed != (tc.stage == InspectionDone) {
			t.Errorf("%s: owed=%v", tc.name, owed)
		}
	}
}

// TestInspectionWaitsWithAPlainReason: a creepjoiner that cannot be ordered an
// inspection stays out of the record and says why.
func TestInspectionWaitsWithAPlainReason(t *testing.T) {
	op := inspectOp("InspectX", 1, 1, true)
	noDoctor, noStock := inspectOp("InspectX", 1, 0, true), inspectOp("InspectX", 1, 1, false)
	busy := inspectable("p", false, op)
	busy.Available = domain.Known(false)
	unread := inspectable("p", false, op)
	unread.Operations = domain.Unknown[[]SurgeryOperation]()
	queued := inspectable("p", false, op)
	queued.QueuedSurgeries, queued.QueuedRecipes = domain.Known(1), []string{"InspectX"}
	hidden := inspectable("p", false, op)
	hidden.Facts.Hediffs = domain.Unknown[[]string]()
	for _, tc := range []struct {
		name    string
		hand    CreepJoinerHand
		recipes InspectionRecipes
		reason  string
	}{
		{"no doctor", inspectable("p", false, noDoctor), inspectionDefs, "no eligible doctor"},
		{"no ingredients", inspectable("p", false, noStock), inspectionDefs, "ingredients are not on the map"},
		{"not offered", inspectable("p", false), inspectionDefs, "offers no surgical inspection"},
		{"no recipe in the game", inspectable("p", false, op), InspectionRecipes{}, "defines no surgical inspection recipe"},
		{"cannot take an order", busy, inspectionDefs, "cannot take an order"},
		{"operations unread", unread, inspectionDefs, "operations could not be read"},
		{"downside unread", hidden, inspectionDefs, "downside could not be read"},
		{"bill queued without a record", queued, inspectionDefs, "already queued"},
	} {
		got := downsideDefs.Inspections([]CreepJoinerHand{tc.hand}, tc.recipes, CreepJoinerRecord{})
		if len(got.Orders) != 0 || len(got.Record.Inspections) != 0 || len(got.Waiting) != 1 || !strings.Contains(got.Waiting[0], tc.reason) {
			t.Errorf("%s: %+v", tc.name, got)
		}
	}
}

// TestCreepJoinerRecordRoundTrips: the record encodes to the goal's string and
// parses back; an unknown stage is refused.
func TestCreepJoinerRecordRoundTrips(t *testing.T) {
	r := CreepJoinerRecord{Inspections: map[PawnID]InspectionStage{"a": InspectionOrdered, "b": InspectionDone}}
	back, err := ParseCreepJoinerRecord(r.Encode())
	if err != nil || len(back.Inspections) != 2 || back.Inspections["a"] != InspectionOrdered || back.Inspections["b"] != InspectionDone {
		t.Fatal(back, err)
	}
	if (CreepJoinerRecord{}).Encode() != "" {
		t.Fatal("an empty record must encode empty")
	}
	if empty, err := ParseCreepJoinerRecord(""); err != nil || len(empty.Inspections) != 0 {
		t.Fatal(empty, err)
	}
	if _, err := ParseCreepJoinerRecord(`{"inspections":{"a":"maybe"}}`); err == nil {
		t.Fatal("an unknown stage was accepted")
	}
}
