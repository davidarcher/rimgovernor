package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestPolicyDatabasesDecode(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	id := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	p, err := DecodeColony(r, id, bridge.Tables{})
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.Policies.Value(); known {
		t.Fatal("absent policy section became known")
	}
	f := &o.PolicyFacts{
		Outfit:       []*o.PolicyEntry{{Id: proto.String("ApparelPolicy_1"), Label: proto.String("Anything"), Default: proto.Bool(true), PawnIds: []string{"Human1", "Human2"}}, {Id: proto.String("ApparelPolicy_2"), Label: proto.String("Worker"), Default: proto.Bool(false)}},
		Reading:      []*o.PolicyEntry{{Id: proto.String("ReadingPolicy_1"), Label: proto.String("All"), Default: proto.Bool(true), PawnIds: []string{"Human1"}}},
		AllowedAreas: []*o.AllowedAreaEntry{{Id: proto.String("Area_3"), Label: proto.String("Area 1"), PawnIds: []string{"Human2"}}},
	}
	r.GetObserved().Policies = &o.PolicySection{Outcome: &o.PolicySection_Observed{Observed: f}}
	if p, err = DecodeColony(r, id, bridge.Tables{}); err != nil {
		t.Fatal(err)
	}
	v, known := p.Policies.Value()
	if !known || len(v.Outfit) != 2 || !v.Outfit[0].Default || len(v.Outfit[0].Pawns) != 2 || v.Outfit[1].Label != "Worker" || len(v.Drug) != 0 || len(v.Reading) != 1 || len(v.AllowedAreas) != 1 || v.AllowedAreas[0].Pawns[0] != "Human2" {
		t.Fatal(v)
	}
	// A pawn holding two policies of one database is a contract failure.
	f.Outfit[1].PawnIds = []string{"Human1"}
	if _, err = DecodeColony(r, id, bridge.Tables{}); err == nil {
		t.Fatal("doubly assigned pawn accepted")
	}
}

func TestPawnPolicyInputsDecode(t *testing.T) {
	if _, known := pawnPolicyInputs(nil).Value(); known {
		t.Fatal("absent inputs became known")
	}
	v, known := pawnPolicyInputs(&o.PawnPolicyInputs{
		ReadingPolicyId: proto.String("ReadingPolicy_1"),
		InventoryStock:  []*o.InventoryStockSetting{{Group: proto.String("Medicine"), ThingDef: proto.String("MedicineHerbal"), Count: proto.Int32(2)}},
		Chemicals:       []*o.ChemicalState{{Chemical: proto.String("Alcohol"), Tolerance: proto.Float64(0.2)}, {Chemical: proto.String("GoJuice"), Addiction: proto.Float64(0.5), Withdrawal: proto.Bool(true)}},
		RoyalTitle:      proto.String("Knight"),
		TitleApparel:    []*o.ApparelRequirementFact{{BodyPartGroups: []string{"Torso"}, RequiredTags: []string{"Royal"}}},
		GuestStatus:     proto.String("Prisoner"), PrisonerInteraction: proto.String("MaintainOnly"),
	}).Value()
	if !known || v.ReadingPolicy != "ReadingPolicy_1" || v.InventoryStock[0].Count != 2 || v.RoyalTitle != "Knight" || v.TitleApparel[0].RequiredTags[0] != "Royal" || v.GuestStatus != "Prisoner" || v.PrisonerInteraction != "MaintainOnly" {
		t.Fatal(v)
	}
	if _, k := v.Chemicals[0].Addiction.Value(); k {
		t.Fatal("absent addiction became known")
	}
	if a, k := v.Chemicals[1].Addiction.Value(); !k || a != 0.5 || !v.Chemicals[1].Withdrawal {
		t.Fatal(v.Chemicals[1])
	}
}
