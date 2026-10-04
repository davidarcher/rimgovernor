package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func careRow(name string, severity, immunity, severityRate, immunityRate float64) CareCondition {
	return CareCondition{DefName: domain.Known(name), Severity: domain.Known(severity), Immunity: domain.Known(immunity),
		SeverityPerDay: domain.Known(severityRate), ImmunityPerDay: domain.Known(immunityRate)}
}

func healthy() domain.Fact[[]CareCondition] { return domain.Known([]CareCondition{}) }

func careChanges(t *testing.T, f RoundsFacts) map[domain.PawnID]domain.MedicalCare {
	t.Helper()
	out := map[domain.PawnID]domain.MedicalCare{}
	for _, s := range MedicalCareChanges(f, DefaultMedicalReservePolicy()) {
		if _, dup := out[s.Pawn()]; dup {
			t.Fatalf("pawn %s written twice", s.Pawn())
		}
		out[s.Pawn()] = s.MedicalCare()
	}
	return out
}

func stock(industrial int64) domain.Fact[[]Amount] {
	return domain.Known([]Amount{{Resource: "MedicineHerbal", Count: 20}, {Resource: "MedicineIndustrial", Count: industrial}, {Resource: "MedicineUltratech", Count: 9}})
}

func TestSeriousCondition(t *testing.T) {
	for _, tt := range []struct {
		name      string
		rows      domain.Fact[[]CareCondition]
		life      domain.Fact[bool]
		want, cnd bool
	}{
		{"healthy", healthy(), domain.Known(false), false, true},
		{"winning flu", domain.Known([]CareCondition{careRow("Flu", .1, .1, .1, .2)}), domain.Known(false), false, true},
		{"losing flu", domain.Known([]CareCondition{careRow("Flu", .7, .2, .2, .1)}), domain.Known(false), true, true},
		{"plague", domain.Known([]CareCondition{careRow("Plague", .1, .1, .1, .2)}), domain.Known(false), true, true},
		{"infection", domain.Known([]CareCondition{careRow("WoundInfection", .1, .1, .1, .2)}), domain.Known(false), true, true},
		{"immune plague", domain.Known([]CareCondition{careRow("Plague", .1, 1, .1, .2)}), domain.Known(false), false, true},
		{"early malaria", domain.Known([]CareCondition{careRow("Malaria", .1, .1, .1, .2)}), domain.Known(false), false, true},
		{"late malaria", domain.Known([]CareCondition{careRow("Malaria", .5, .6, .1, .2)}), domain.Known(false), true, true},
		{"life threat", domain.Unknown[[]CareCondition](), domain.Known(true), true, true},
		{"unknown list", domain.Unknown[[]CareCondition](), domain.Known(false), false, false},
		{"unknown rates", domain.Known([]CareCondition{{DefName: domain.Known("Flu"), Severity: domain.Known(.1), Immunity: domain.Known(.1)}}), domain.Known(false), false, false},
	} {
		got, known := SeriousCondition(tt.rows, tt.life)
		if got != tt.want || known != tt.cnd {
			t.Errorf("%s: serious %v known %v", tt.name, got, known)
		}
	}
}

// Colonists: Normal with industrial stock at the per-colonist target, Herbal
// below it, one tier up while serious, Best only through the raise.
func TestColonistCareCap(t *testing.T) {
	colonist := func(id, care string, rows domain.Fact[[]CareCondition]) CarePawn {
		return CarePawn{ID: PawnID(id), Care: domain.Known(care), Dead: domain.Known(false), Conditions: rows, LifeThreatening: domain.Known(false)}
	}
	plague := domain.Known([]CareCondition{careRow("Plague", .1, .1, .1, .2)})
	pawns := domain.Known([]CarePawn{colonist("a", "Best", healthy()), colonist("b", "NormalOrWorse", plague), colonist("c", "NormalOrWorse", domain.Unknown[[]CareCondition]())})
	stocked := careChanges(t, RoundsFacts{Items: CoreItemFacts(), MedicalPawns: pawns, Resources: stock(9)})
	if len(stocked) != 2 || stocked["a"] != domain.CareNormal || stocked["b"] != domain.CareBest {
		t.Fatalf("stocked %v", stocked)
	}
	short := careChanges(t, RoundsFacts{Items: CoreItemFacts(), MedicalPawns: pawns, Resources: stock(8)})
	if len(short) != 1 || short["a"] != domain.CareHerbal {
		t.Fatalf("short %v", short)
	}
	if got := careChanges(t, RoundsFacts{Items: CoreItemFacts(), MedicalPawns: pawns, Resources: domain.Unknown[[]Amount]()}); len(got) != 0 {
		t.Fatalf("unknown stock wrote %v", got)
	}
}

// Prisoners by use; harvest and execution targets are never raised.
func TestPrisonerCareCap(t *testing.T) {
	plague := domain.Known([]CareCondition{careRow("Plague", .1, .1, .1, .2)})
	prisoner := func(id string, mode domain.PrisonerInteractionMode, rows domain.Fact[[]CareCondition]) PrisonerFacts {
		return PrisonerFacts{Pawn: domain.PawnID(id), Dead: domain.Known(false), MedicalCare: domain.Known("NoMeds"), CurrentInteraction: domain.Known(mode), Conditions: rows, LifeThreatening: domain.Known(false)}
	}
	harvest := prisoner("h", domain.PrisonerInteractionMaintain, plague)
	harvest.QueuedRecipes = []string{"Harvest"}
	harvest.Operations = domain.Known([]SurgeryOperation{{Recipe: domain.Known("Harvest"), Kind: SurgeryHarvest}})
	execute := prisoner("x", "", plague)
	execute.Executing = true
	rows := []PrisonerFacts{
		prisoner("r", domain.PrisonerInteractionRecruit, healthy()), prisoner("rr", domain.PrisonerInteractionReduceResistance, healthy()),
		prisoner("c", domain.PrisonerInteractionConvert, healthy()), prisoner("e", domain.PrisonerInteractionEnslave, plague),
		prisoner("m", domain.PrisonerInteractionMaintain, healthy()), prisoner("rel", domain.PrisonerInteractionRelease, plague),
		harvest, execute,
	}
	got := careChanges(t, RoundsFacts{Items: CoreItemFacts(), Prisoners: domain.Known(rows)})
	want := map[domain.PawnID]domain.MedicalCare{"r": domain.CareNormal, "rr": domain.CareNormal, "c": domain.CareNormal, "e": domain.CareBest,
		"m": domain.CareHerbal, "rel": domain.CareNormal, "h": domain.CareHerbal, "x": domain.CareHerbal}
	for id, care := range want {
		if got[id] != care {
			t.Errorf("%s: %s want %s", id, got[id], care)
		}
	}
}

// Guests hold Normal; animals Herbal, bonded or Release-trained Normal.
func TestGuestAndAnimalCareCap(t *testing.T) {
	guests := domain.Known([]CarePatient{{ID: "g", Care: domain.Known("HerbalOrWorse"), Conditions: healthy(), LifeThreatening: domain.Known(false)}})
	animal := func(id string, bonded bool, release bool) UpkeepAnimal {
		return UpkeepAnimal{ID: PawnID(id), Care: domain.Known("Best"), Bonded: domain.Known(bonded), Conditions: healthy(), LifeThreatening: domain.Known(false),
			Training: []HusbandryTrainable{{Def: "Release", Learned: domain.Known(release)}}}
	}
	unread := animal("u", false, false)
	unread.Bonded = domain.Unknown[bool]()
	f := RoundsFacts{Items: CoreItemFacts(), Guests: guests, AnimalUpkeep: AnimalUpkeepObservation{Animals: domain.Known([]UpkeepAnimal{animal("plain", false, false), animal("pet", true, false), animal("dog", false, true), unread})}}
	got := careChanges(t, f)
	want := map[domain.PawnID]domain.MedicalCare{"g": domain.CareNormal, "plain": domain.CareHerbal, "pet": domain.CareNormal, "dog": domain.CareNormal}
	if len(got) != len(want) {
		t.Fatalf("%v", got)
	}
	for id, care := range want {
		if got[id] != care {
			t.Errorf("%s: %s want %s", id, got[id], care)
		}
	}
	if owed, _ := MedicalCareOwed(f, DefaultMedicalReservePolicy()).Value(); !owed {
		t.Fatal("care not owed")
	}
}
