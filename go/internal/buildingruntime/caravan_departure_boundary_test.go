package buildingruntime

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func caravanCatalogGroups() []*n.CargoGroup {
	return []*n.CargoGroup{
		{GroupId: proto.String("g-wood"), DefName: proto.String("WoodLog"), Count: proto.Int64(75)},
		{GroupId: proto.String("g-pemmican"), DefName: proto.String("Pemmican"), Count: proto.Int64(40), Nutrition: proto.Float64(0.8), Perishable: proto.Bool(true), RotDays: proto.Float64(58.5), Reserve: proto.Bool(true), EaterIds: []string{"alpha", "beta"}},
	}
}

// TestCaravanCargoGroupsCarryFoodFacts: the catalog's food facts reach the
// policy as plain values and a non-food group keeps its nutrition unknown.
func TestCaravanCargoGroupsCarryFoodFacts(t *testing.T) {
	groups := caravanCargoGroups(caravanCatalogGroups())
	if len(groups) != 2 {
		t.Fatal(groups)
	}
	if _, known := groups[0].Nutrition.Value(); known || groups[0].Perishable || groups[0].Reserve || len(groups[0].Eaters) != 0 {
		t.Fatal(groups[0])
	}
	nutrition, _ := groups[1].Nutrition.Value()
	rot, _ := groups[1].RotDays.Value()
	if groups[1].Count != 40 || nutrition != 0.8 || !groups[1].Perishable || rot != 58.5 || !groups[1].Reserve || len(groups[1].Eaters) != 2 || groups[1].Eaters[0] != domain.PawnID("alpha") {
		t.Fatal(groups[1])
	}
}

// TestCaravanCargoSelectionsRefuseStaleLines: an admitted line the live
// catalog no longer offers in full, or under another definition, is stale
// evidence rather than a truncated load.
func TestCaravanCargoSelectionsRefuseStaleLines(t *testing.T) {
	lines := []store.CaravanCargoAdmission{{GroupID: "g-wood", Definition: "WoodLog", Count: 50}, {GroupID: "g-pemmican", Definition: "Pemmican", Count: 40}}
	selections, err := caravanCargoSelections(lines, caravanCatalogGroups())
	if err != nil || len(selections) != 2 || selections[0].GroupID != "g-wood" || selections[0].Count != 50 || selections[1].Count != 40 {
		t.Fatal(selections, err)
	}
	for name, change := range map[string]func([]store.CaravanCargoAdmission) []store.CaravanCargoAdmission{
		"eaten": func(l []store.CaravanCargoAdmission) []store.CaravanCargoAdmission { l[1].Count = 41; return l },
		"regrouped": func(l []store.CaravanCargoAdmission) []store.CaravanCargoAdmission {
			l[1].GroupID = "g-meals"
			return l
		},
		"redefined": func(l []store.CaravanCargoAdmission) []store.CaravanCargoAdmission {
			l[1].Definition = "MealSimple"
			return l
		},
		"zero count": func(l []store.CaravanCargoAdmission) []store.CaravanCargoAdmission { l[0].Count = 0; return l },
	} {
		t.Run(name, func(t *testing.T) {
			stale := change(append([]store.CaravanCargoAdmission(nil), lines...))
			if _, err := caravanCargoSelections(stale, caravanCatalogGroups()); !errors.Is(err, executor.ErrEvidence) {
				t.Fatal(err)
			}
		})
	}
}

func TestHomeDoctorAvailableIgnoresCrew(t *testing.T) {
	doctor := func(id string, disabled bool) *n.PawnState {
		return &n.PawnState{Pawn: &n.EntityRef{Id: proto.String(id)}, Downed: proto.Bool(false), Settings: &n.PawnSettings{Work: []*n.WorkSetting{{DefName: proto.String("Doctor"), Disabled: proto.Bool(disabled)}}}}
	}
	crew := map[domain.PawnID]bool{"alpha": true}
	if v, known := homeDoctorAvailable([]*n.PawnState{doctor("alpha", false), doctor("beta", true)}, crew).Value(); !known || v {
		t.Fatal("departing doctor counted as home coverage")
	}
	if v, known := homeDoctorAvailable([]*n.PawnState{doctor("alpha", false), doctor("beta", false)}, crew).Value(); !known || !v {
		t.Fatal("home doctor not counted")
	}
	if _, known := homeDoctorAvailable([]*n.PawnState{doctor("alpha", false), {Pawn: &n.EntityRef{Id: proto.String("beta")}}}, crew).Value(); known {
		t.Fatal("unknown home pawn proved false")
	}
}
