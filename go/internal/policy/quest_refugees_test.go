package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func TestPodRefugeeCustodyTendAndRecruit(t *testing.T) {
	row := CustodyFacts{Pawn: "refugee", QuestRefugee: true, Dead: domain.Known(false), Downed: domain.Known(true), Guest: domain.Known(false), Admitted: domain.Known(false), Prisoner: domain.Known(false), Hostile: domain.Known(false), WearingApparel: domain.Known(true)}
	if got := SelectCustodyMethod(domain.Known([]CustodyFacts{row})); got.Decision != CustodyRescue {
		t.Fatal(got)
	}
	row.Hostile = domain.Known(true)
	if got := SelectCustodyMethod(domain.Known([]CustodyFacts{row})); got.Decision != CustodyCapture {
		t.Fatalf("hostile clothed refugee must be secured: %+v", got)
	}
	patient := tendPatient(row.Pawn, 2)
	patient.Downed = domain.Known(true)
	doctor, target, ok := selectTend([]TendDoctorFacts{tendDoctor("doctor", 10)}, []TendPatientFacts{patient})
	if !ok || target != row.Pawn || doctor != "doctor" {
		t.Fatal(doctor, target, ok)
	}
	prospect := prisonerRow(string(row.Pawn), true, domain.PrisonerInteractionMaintain, 5, 1, strong)
	got := SelectPrisonerInteractionMethod(domain.Known([]PrisonerFacts{prospect}), domain.Known(core), domain.Known(20.0), PrisonerPolicy{FoodTargetDays: 7, ReleaseAfterDays: 10})
	if got.Interaction != domain.PrisonerInteractionRecruit {
		t.Fatal(got)
	}
}

func TestPodRefugeeScopeExcludesGhoulAndCompletedQuest(t *testing.T) {
	row := JoinerOffer{Quest: "quest", State: "Ongoing", ScriptDef: "RefugeePodCrash_Baby", Profile: domain.Known(QuestFamilyForRoot("RefugeePodCrash_Baby")), Objectives: []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_RESCUE_PAWNS, PawnIDs: []domain.PawnID{"baby"}}}}
	if ids := QuestRefugeeIDs(domain.Known([]JoinerOffer{row})); ids["baby"] != row.Quest {
		t.Fatal(ids)
	}
	row.ScriptDef = "RefugeePodCrash_Ghoul"
	row.Profile = domain.Known(QuestFamilyForRoot(row.ScriptDef))
	if ids := QuestRefugeeIDs(domain.Known([]JoinerOffer{row})); len(ids) != 0 {
		t.Fatal(ids)
	}
	row.ScriptDef = "RefugeePodCrash"
	row.Profile = domain.Known(QuestFamilyForRoot(row.ScriptDef))
	row.State = "Success"
	if ids := QuestRefugeeIDs(domain.Known([]JoinerOffer{row})); len(ids) != 0 {
		t.Fatal(ids)
	}
}
