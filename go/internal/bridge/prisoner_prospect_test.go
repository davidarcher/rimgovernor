package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A colony prisoner's organ harvest facts (#1169) ride on its population
// row; the snapshot carries the OrganUse precept.
func TestPopulationDecodesHarvestFacts(t *testing.T) {
	prisoner := prisonerPerson("p", "MaintainOnly")
	prisoner.Faction, prisoner.HarvestGoodwillChange, prisoner.Withdrawal = &commonpb.Ref{Id: proto.String("Faction_3")}, proto.Int32(-70), proto.Bool(true)
	prisoner.Surgery = &o.PawnHealth{SurgeryBills: []*o.SurgeryBill{{Id: proto.String("Bill_1")}}, Operations: []*o.SurgeryOperation{{
		Recipe: &o.DefinitionRef{DefName: proto.String("RemoveBodyPart")}, PartIndex: proto.Int32(20), PartDefName: proto.String("Kidney"),
		Kind: o.SurgeryKind_SURGERY_KIND_HARVEST, YieldMarketValue: proto.Float64(900)}}}
	prisoner.PolicyInputs = &o.PawnPolicyInputs{DrugPolicyId: proto.String("DrugPolicy_2"), GuestStatus: proto.String("Prisoner"),
		Chemicals: []*o.ChemicalState{{Chemical: proto.String("Alcohol"), Addiction: proto.Float64(0.4), Withdrawal: proto.Bool(false)}}}
	snapshot := populationReply(prisoner, prisonerPerson("q", "")).GetObserved()
	snapshot.OrganUsePrecept = proto.String("OrganUse_Acceptable")
	census, err := decodePopulation(snapshot, populationPawns(snapshot))
	if err != nil {
		t.Fatal(err)
	}
	colony, _ := census.Colony.Value()
	rows, _ := census.Prisoners.Value()
	if colony.OrganUsePrecept != "OrganUse_Acceptable" || len(rows) != 2 {
		t.Fatalf("colony %+v rows %+v", colony, rows)
	}
	ops, ok := rows[0].Operations.Value()
	queued, qk := rows[0].QueuedSurgeries.Value()
	goodwill, gk := rows[0].HarvestGoodwill.Value()
	if !ok || len(ops) != 1 || !qk || queued != 1 || !gk || goodwill != -70 || rows[0].Faction != "Faction_3" {
		t.Fatalf("row %+v", rows[0])
	}
	if value, known := ops[0].YieldValue.Value(); !known || value != 900 {
		t.Fatalf("op %+v", ops[0])
	}
	if withdrawal, known := rows[0].Withdrawal.Value(); !known || !withdrawal {
		t.Fatalf("withdrawal %+v", rows[0].Withdrawal)
	}
	if _, known := rows[1].Operations.Value(); known {
		t.Fatal("a producer without surgery facts leaves them unknown")
	}
	inputs, known := rows[0].PolicyInputs.Value()
	if !known || inputs.DrugPolicy != "DrugPolicy_2" || len(inputs.Chemicals) != 1 || inputs.Chemicals[0].Chemical != "Alcohol" {
		t.Fatalf("policy inputs %+v", rows[0].PolicyInputs)
	}
	if _, known := rows[1].PolicyInputs.Value(); known {
		t.Fatal("a producer without policy inputs leaves them unknown")
	}
}

// The population read carries each prisoner's prospect and the colony side
// MaintainPopulation weighs it against (#1036).
func TestPopulationDecodesProspectAndColony(t *testing.T) {
	skill := func(name string, level int32) *o.Skill {
		return &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String(name)}, Level: proto.Int32(level), Passion: o.Passion_PASSION_MAJOR.Enum()}
	}
	prisoner := prisonerPerson("p", "MaintainOnly")
	prisoner.Will, prisoner.IdeoId, prisoner.WildMan, prisoner.HealthSummary = proto.Float64(3), proto.String("Ideo_2"), proto.Bool(false), proto.Float64(0.9)
	prisoner.Biography = &o.PawnBiography{BiologicalAgeYears: proto.Float64(30), Skills: []*o.Skill{skill("Cooking", 9)}, Traits: []*o.Trait{{DefName: proto.String("Tough"), Degree: proto.Int32(0)}}, IncapableWorkTypes: []string{"Mining"}}
	colonist := &o.PopulationPerson{Pawn: &commonpb.Ref{Id: proto.String("c")}, Admitted: proto.Bool(true),
		Biography: &o.PawnBiography{BiologicalAgeYears: proto.Float64(40), Skills: []*o.Skill{skill("Cooking", 4), skill("Mining", 11)}}}
	reply := populationReply(prisoner, colonist)
	snapshot := reply.GetObserved()
	snapshot.IdeologyActive, snapshot.ColonyIdeoId, snapshot.SlaveryPrecept = proto.Bool(true), proto.String("Ideo_1"), proto.String("Slavery_Acceptable")
	census, err := decodePopulation(snapshot, populationPawns(snapshot))
	if err != nil {
		t.Fatal(err)
	}
	colony, known := census.Colony.Value()
	if !known || colony.Colonists != 1 || colony.BestSkill["Cooking"] != 4 || colony.BestSkill["Mining"] != 11 || len(colony.Medicine) != 0 || !colony.SlaveryAllowed() || colony.Ideo != "Ideo_1" {
		t.Fatalf("colony %+v", colony)
	}
	rows, _ := census.Prisoners.Value()
	if len(rows) != 1 {
		t.Fatal(rows)
	}
	row := rows[0]
	prospect, pk := row.Prospect.Value()
	will, wk := row.Will.Value()
	if !pk || prospect.Age != 30 || prospect.Health != 0.9 || len(prospect.Skills) != 1 || prospect.Skills[0].Level != 9 || len(prospect.Traits) != 1 || len(prospect.Incapable) != 1 || !wk || will != 3 || row.Ideo != "Ideo_2" {
		t.Fatalf("row %+v prospect %+v", row, prospect)
	}
	// A producer without the biography leaves the prospect unknown.
	q := populationReply(prisonerPerson("q", "")).GetObserved()
	census, _ = decodePopulation(q, populationPawns(q))
	rows, _ = census.Prisoners.Value()
	if _, known := rows[0].Prospect.Value(); known {
		t.Fatal("prospect should be unknown")
	}
}
