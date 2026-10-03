package bridge

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func wood(n int64) []policy.Amount { return []policy.Amount{{Resource: "WoodLog", Count: n}} }

func diningDefs(extra ...FixtureDef) []FixtureDef {
	pin := FixtureDef{Name: "Pin", Joy: &FixtureJoy{Kind: "Gaming_Dexterity", WatchGiver: true}}
	return append([]FixtureDef{
		// Comfort per cost: stool .02, chair .0156, armchair .0073.
		{Name: "Stool", Sittable: true, Comfort: .5, Costs: wood(25)},
		{Name: "Chair", Sittable: true, Comfort: .7, Costs: wood(45)},
		{Name: "Armchair", Sittable: true, Comfort: .8, Costs: wood(110)},
		{Name: "SmallTable", Width: 1, Height: 2, EatSurface: true, Costs: wood(20)},
		{Name: "CheapBigTable", Width: 3, Height: 3, EatSurface: true, Costs: wood(5)},
		{Name: "DearTable", Width: 1, Height: 2, EatSurface: true, Costs: wood(40)},
		pin,
	}, extra...)
}

func TestDiningFurnitureIsARuleOverTheRows(t *testing.T) {
	got, err := FixtureCatalog("load", diningDefs()...).DiningFurniture()
	if err != nil {
		t.Fatal(err)
	}
	if got.Chair.Def != "Stool" || got.Table.Def != "SmallTable" || got.Pin.Def != "Pin" || got.Lane != 6 {
		t.Errorf("dining furniture %+v", got)
	}
	if got.Table.Size.X != 1 || got.Table.Size.Z != 2 {
		t.Errorf("table size %+v", got.Table.Size)
	}
	// A pricier-per-comfort stool gives the chair to the next best.
	defs := diningDefs()
	defs[0].Costs = wood(100)
	if got, err = FixtureCatalog("load", defs...).DiningFurniture(); err != nil || got.Chair.Def != "Chair" {
		t.Errorf("chair %+v, %v", got.Chair, err)
	}
}

func TestDiningFurnitureRefusesWhatItCannotFindOrLayOut(t *testing.T) {
	pin := FixtureDef{Name: "Pin", Joy: &FixtureJoy{Kind: "Gaming_Dexterity", WatchGiver: true}}
	for name, test := range map[string]struct {
		defs []FixtureDef
		want string
	}{
		"no chair":  {[]FixtureDef{{Name: "T", Width: 1, Height: 2, EatSurface: true, Costs: wood(5)}, pin}, "sittable"},
		"no table":  {[]FixtureDef{{Name: "C", Sittable: true, Comfort: .5, Costs: wood(5)}, pin}, "eating surface"},
		"no pin":    {[]FixtureDef{{Name: "C", Sittable: true, Comfort: .5, Costs: wood(5)}, {Name: "T", Width: 1, Height: 2, EatSurface: true, Costs: wood(5)}}, "joy building"},
		"odd table": {[]FixtureDef{{Name: "C", Sittable: true, Comfort: .5, Costs: wood(5)}, {Name: "T", Width: 2, Height: 2, EatSurface: true, Costs: wood(5)}, pin}, "2x2"},
	} {
		_, err := FixtureCatalog("load", test.defs...).DiningFurniture()
		if err == nil || !strings.Contains(err.Error(), test.want) {
			t.Errorf("%s: %v, want an error naming %q", name, err, test.want)
		}
	}
	if _, err := (*DefinitionCatalog)(nil).DiningFurniture(); err == nil {
		t.Error("a nil catalog gave dining furniture")
	}
}
