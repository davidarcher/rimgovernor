package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func drugPawn(id string, age float64, traits []PawnTrait, chemicals ...ChemicalState) WorkPawn {
	return WorkPawn{ID: PawnID(id), Age: domain.Known(age), Traits: domain.Known(traits),
		PolicyInputs: domain.Known(PawnPolicyInputs{DrugPolicy: "DrugPolicy_1", Chemicals: chemicals})}
}

func joyDrugs(t *testing.T, pawn WorkPawn) []string {
	t.Helper()
	entries, ok := DrugEntries(pawn, CoreItemFacts(), false)
	if !ok {
		t.Fatal("unknown")
	}
	var defs []string
	for _, e := range entries {
		if !e.Joy || e.Addiction || e.Scheduled || e.TakeToInventory != 0 {
			t.Fatal(e)
		}
		defs = append(defs, e.Drug)
	}
	return defs
}

func TestDrugEntriesPerInput(t *testing.T) {
	if got := joyDrugs(t, drugPawn("adult", 30, nil)); len(got) != 3 || got[0] != "Beer" || got[1] != "SmokeleafJoint" || got[2] != "PsychiteTea" {
		t.Fatal("plain adult", got)
	}
	if got := joyDrugs(t, drugPawn("child", 9, nil)); len(got) != 0 {
		t.Fatal("child", got)
	}
	for _, degree := range []int{-1, 1, 2} {
		if got := joyDrugs(t, drugPawn("trait", 30, []PawnTrait{testTrait("DrugDesire", degree)})); len(got) != 0 {
			t.Fatal("DrugDesire", degree, got)
		}
	}
	tolerant := ChemicalState{Chemical: "Alcohol", Tolerance: domain.Known(0.6)}
	if got := joyDrugs(t, drugPawn("tolerant", 30, nil, tolerant)); len(got) != 2 || got[0] != "SmokeleafJoint" {
		t.Fatal("high tolerance", got)
	}
	low := ChemicalState{Chemical: "Alcohol", Tolerance: domain.Known(0.2)}
	if got := joyDrugs(t, drugPawn("low", 30, nil, low)); len(got) != 3 {
		t.Fatal("low tolerance", got)
	}
	addicted := ChemicalState{Chemical: "Psychite", Addiction: domain.Known(0.3)}
	if got := joyDrugs(t, drugPawn("addict", 30, nil, addicted)); len(got) != 2 || got[1] != "SmokeleafJoint" {
		t.Fatal("addiction", got)
	}
	unknown := drugPawn("unknown", 30, nil)
	unknown.Traits = domain.Unknown[[]PawnTrait]()
	if _, ok := DrugEntries(unknown, CoreItemFacts(), false); ok {
		t.Fatal("unknown traits planned")
	}
}

func TestDrugPolicyChanges(t *testing.T) {
	adult, child := drugPawn("adult", 30, nil), drugPawn("child", 9, nil)
	names := []OwnedName{{Pawn: "adult", Short: "Bob"}, {Pawn: "child", Short: "Tim"}}
	changes := DrugPolicyChanges(CoreItemFacts(), []WorkPawn{adult, child}, nil, names, nil, domain.Unknown[[]Amount](), nil, SoldierSquad{})
	if len(changes) != 2 || changes[0].Write == nil || changes[0].Write.Name() != "Bob" || len(changes[0].Write.Entries()) != 3 || changes[0].Assign == nil {
		t.Fatal(changes)
	}
	if changes[1].Write == nil || len(changes[1].Write.Entries()) != 0 || changes[1].Assign == nil {
		t.Fatal(changes[1])
	}
	// The held policy already carrying the contents owes nothing, in any
	// observed order.
	held := []DrugPolicyEntry{
		{ID: "DrugPolicy_1", Label: "Bob", Entries: []domain.DrugPolicyEntry{joyEntry("SmokeleafJoint"), joyEntry("Beer"), joyEntry("PsychiteTea")}},
		{ID: "DrugPolicy_2", Label: "Tim"},
	}
	if got := DrugPolicyChanges(CoreItemFacts(), []WorkPawn{adult}, nil, names, held, domain.Unknown[[]Amount](), nil, SoldierSquad{}); len(got) != 0 {
		t.Fatal(got)
	}
	// The child's policy exists but is not held: assign only.
	if got := DrugPolicyChanges(CoreItemFacts(), []WorkPawn{child}, nil, names, held, domain.Unknown[[]Amount](), nil, SoldierSquad{}); len(got) != 1 || got[0].Write != nil || got[0].Assign == nil {
		t.Fatal(got)
	}
	// A shared short name waits for the rename.
	if got := DrugPolicyChanges(CoreItemFacts(), []WorkPawn{adult}, nil, append(names, OwnedName{Pawn: "other", Short: "bob"}), nil, domain.Unknown[[]Amount](), nil, SoldierSquad{}); len(got) != 0 {
		t.Fatal(got)
	}
}

func TestDrugEntriesPreventivesAndDependency(t *testing.T) {
	scheduled := func(e []domain.DrugPolicyEntry) map[string]float64 {
		r := map[string]float64{}
		for _, x := range e {
			if x.Scheduled {
				r[x.Drug] = x.DaysFrequency
			}
		}
		return r
	}
	if !DiseaseBiome(CoreItemFacts(), []string{"Flu", "Malaria"}) || DiseaseBiome(CoreItemFacts(), []string{"Flu"}) {
		t.Fatal("disease biome")
	}
	for _, p := range []WorkPawn{drugPawn("adult", 30, nil), drugPawn("child", 9, nil), drugPawn("tee", 30, []PawnTrait{testTrait("DrugDesire", -1)})} {
		e, _ := DrugEntries(p, CoreItemFacts(), true)
		if s := scheduled(e); len(s) != 1 || s["Penoxycyline"] != 5 {
			t.Fatal("penoxycyline", p.ID, s)
		}
	}
	dep := drugPawn("dep", 30, nil)
	in, _ := dep.PolicyInputs.Value()
	in.DependencyChemicals = []string{"GoJuice", "Alcohol"}
	dep.PolicyInputs = domain.Known(in)
	e, _ := DrugEntries(dep, CoreItemFacts(), false)
	s := scheduled(e)
	if len(s) != 2 || s["GoJuice"] != DependencyDays || s["Beer"] != DependencyDays {
		t.Fatal("dependency", s)
	}
	if _, err := domain.NewDrugPolicy("dep", e); err != nil {
		t.Fatal(err)
	}
	e, _ = DrugEntries(drugPawn("adult", 30, nil), CoreItemFacts(), false)
	if s := scheduled(e); len(s) != 0 {
		t.Fatal("neither", s)
	}
	if got := DrugPolicyChanges(CoreItemFacts(), []WorkPawn{drugPawn("adult", 30, nil)}, nil, []OwnedName{{Pawn: "adult", Short: "ann"}}, nil, domain.Unknown[[]Amount](), []string{"Plague"}, SoldierSquad{}); len(got) != 1 || scheduled(got[0].Write.Entries())["Penoxycyline"] != 5 {
		t.Fatal("changes", got)
	}
}

func TestAddictionEntries(t *testing.T) {
	lucy := drugPawn("lucy", 30, nil, ChemicalState{Chemical: "Luciferium", Addiction: domain.Known(0.5)})
	got := AddictionEntries(lucy, CoreItemFacts(), map[string]int64{"Luciferium": 100})
	if len(got) != 1 || got[0].Drug != "Luciferium" || !got[0].Addiction || !got[0].Scheduled || got[0].DaysFrequency != 4 {
		t.Fatal("luciferium maintained", got)
	}
	beer := drugPawn("beer", 30, nil, ChemicalState{Chemical: "Alcohol", Addiction: domain.Known(0.5)})
	stock := map[string]int64{"Beer": 10}
	got = AddictionEntries(beer, CoreItemFacts(), stock)
	if len(got) != 1 || got[0].Drug != "Beer" || got[0].Addiction || !got[0].Scheduled || got[0].DaysFrequency != 4 || stock["Beer"] != 6 {
		t.Fatal("weaned with supply", got, stock)
	}
	// Too little stock to cover the wean: maintained.
	got = AddictionEntries(beer, CoreItemFacts(), map[string]int64{"Beer": 3})
	if len(got) != 1 || !got[0].Addiction || got[0].DaysFrequency != 2 {
		t.Fatal("short supply maintained", got)
	}
	if got := AddictionEntries(beer, CoreItemFacts(), map[string]int64{}); len(got) != 0 {
		t.Fatal("no drug scheduled", got)
	}
	// A child is maintained too; unknown stock maintains.
	child := drugPawn("child", 9, nil, ChemicalState{Chemical: "Psychite", Addiction: domain.Known(0.9)})
	if got := AddictionEntries(child, CoreItemFacts(), nil); len(got) != 1 || got[0].Drug != "PsychiteTea" || !got[0].Addiction {
		t.Fatal("child", got)
	}
	if got := AddictionEntries(child, CoreItemFacts(), map[string]int64{"Yayo": 1}); len(got) != 1 || got[0].Drug != "Yayo" {
		t.Fatal("stocked alternative", got)
	}
	if WeanInterval(CoreItemFacts().AddictionDrugs()[0], 0.1) != 8 || WeanInterval(CoreItemFacts().AddictionDrugs()[0], 1) != 2 {
		t.Fatal("widening")
	}
}

func TestDrugPolicyChangesAllotsStock(t *testing.T) {
	a := drugPawn("a", 30, nil, ChemicalState{Chemical: "Alcohol", Addiction: domain.Known(0.5)})
	b := drugPawn("b", 9, nil, ChemicalState{Chemical: "Alcohol", Addiction: domain.Known(0.5)})
	names := []OwnedName{{Pawn: "a", Short: "Ann"}, {Pawn: "b", Short: "Ben"}}
	changes := DrugPolicyChanges(CoreItemFacts(), []WorkPawn{a, b}, nil, names, nil, domain.Known([]Amount{{Resource: "Beer", Count: 5}}), nil, SoldierSquad{})
	if len(changes) != 2 {
		t.Fatal(changes)
	}
	weaned, maintained := changes[0].Write.Entries(), changes[1].Write.Entries()
	if len(weaned) != 3 || weaned[0].Drug != "Beer" || weaned[0].Addiction || len(maintained) != 1 || !maintained[0].Addiction {
		t.Fatal(weaned, maintained)
	}
}

func TestDependencyAndAddictionMerge(t *testing.T) {
	p := drugPawn("p", 30, nil, ChemicalState{Chemical: "Alcohol", Addiction: domain.Known(0.5)})
	in, _ := p.PolicyInputs.Value()
	in.DependencyChemicals = []string{"Alcohol"}
	p.PolicyInputs = domain.Known(in)
	got := DrugPolicyChanges(CoreItemFacts(), []WorkPawn{p}, nil, []OwnedName{{Pawn: "p", Short: "Pat"}}, nil, domain.Unknown[[]Amount](), nil, SoldierSquad{})
	if len(got) != 1 || got[0].Write == nil {
		t.Fatal(got)
	}
	for _, e := range got[0].Write.Entries() {
		if e.Drug == "Beer" && (!e.Addiction || !e.Scheduled || e.DaysFrequency != 2) {
			t.Fatal(e)
		}
	}
}

// A prisoner holds its own policy that allows no recreation, only the
// maintenance its addiction owes; one with none holds an empty policy.
func TestDrugPolicyChangesPrisoners(t *testing.T) {
	inputs := func(chemicals ...ChemicalState) domain.Fact[PawnPolicyInputs] {
		return domain.Known(PawnPolicyInputs{DrugPolicy: "DrugPolicy_1", GuestStatus: "Prisoner", Chemicals: chemicals})
	}
	prisoners := []PrisonerFacts{
		{Pawn: "addict", PolicyInputs: inputs(ChemicalState{Chemical: "Alcohol", Addiction: domain.Known(0.5)})},
		{Pawn: "clean", PolicyInputs: inputs()},
		{Pawn: "unread"},
	}
	names := []OwnedName{{Pawn: "addict", Short: "Ada"}, {Pawn: "clean", Short: "Cal"}, {Pawn: "unread", Short: "Uma"}}
	changes := DrugPolicyChanges(CoreItemFacts(), nil, prisoners, names, nil, domain.Unknown[[]Amount](), nil, SoldierSquad{})
	if len(changes) != 2 || changes[0].Write.Name() != "Ada" || changes[1].Write.Name() != "Cal" {
		t.Fatal(changes)
	}
	if got := changes[0].Write.Entries(); len(got) != 1 || got[0].Joy || !got[0].Addiction || !got[0].Scheduled {
		t.Fatal("addict", got)
	}
	if got := changes[1].Write.Entries(); len(got) != 0 {
		t.Fatal("clean", got)
	}
	// Held with exactly its contents, a prisoner owes nothing.
	held := []DrugPolicyEntry{{ID: "DrugPolicy_1", Label: "Cal", Pawns: []PawnID{"clean"}}}
	if got := DrugPolicyChanges(CoreItemFacts(), nil, prisoners[1:2], names, held, domain.Unknown[[]Amount](), nil, SoldierSquad{}); len(got) != 0 {
		t.Fatal(got)
	}
}
