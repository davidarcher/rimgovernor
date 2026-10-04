package policy

import (
	"errors"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestAnimalExposures(t *testing.T) {
	ranges := map[Resource]AnimalComfort{"Muffalo": {Min: -40, Max: 50}, "Thrumbo": {Min: -10, Max: 30}}
	comfort := func(r Resource) (AnimalComfort, error) {
		if c, ok := ranges[r]; ok {
			return c, nil
		}
		return AnimalComfort{}, errors.New("no stat")
	}
	census := func(defs ...string) domain.Fact[[]DisasterCondition] {
		rows := []DisasterCondition{}
		for _, d := range defs {
			rows = append(rows, DisasterCondition{ID: d, Definition: d})
		}
		return domain.Known(rows)
	}
	both := func(c ...ExposureCause) map[Resource][]ExposureCause {
		return map[Resource][]ExposureCause{"Muffalo": c, "Thrumbo": c}
	}
	races := []Resource{"Thrumbo", "Muffalo", "Thrumbo"}
	cases := []struct {
		name  string
		conds domain.Fact[[]DisasterCondition]
		temp  float64
		want  map[Resource][]ExposureCause
	}{
		{"calm", census(), 20, both()},
		{"cold snap", census("ColdSnap"), 20, both(ExposureCold)},
		{"heat wave", census("HeatWave"), 20, both(ExposureHeat)},
		{"fallout", census("ToxicFallout", "Eclipse"), 20, both(ExposureFallout)},
		{"on the lower edge", census(), -10, both()},
		{"just below thrumbo", census(), -10.1, map[Resource][]ExposureCause{"Thrumbo": {ExposureCold}}},
		{"on the upper edge", census(), 30, both()},
		{"just above thrumbo", census(), 30.1, map[Resource][]ExposureCause{"Thrumbo": {ExposureHeat}}},
		{"snap and cold agree", census("ColdSnap"), -50, both(ExposureCold)},
		{"fallout and heat", census("ToxicFallout"), 60, map[Resource][]ExposureCause{"Muffalo": {ExposureFallout, ExposureHeat}, "Thrumbo": {ExposureFallout, ExposureHeat}}},
	}
	for _, c := range cases {
		got, err := AnimalExposures(races, c.conds, domain.Known(c.temp), comfort)
		if err != nil || len(got) != 2 || got[0].Race != "Muffalo" {
			t.Fatalf("%s: %v %v", c.name, got, err)
		}
		for _, e := range got {
			if !slices.Equal(e.Causes, c.want[e.Race]) {
				t.Errorf("%s: %s causes %v, want %v", c.name, e.Race, e.Causes, c.want[e.Race])
			}
			if e.Danger() != (len(c.want[e.Race]) > 0) {
				t.Errorf("%s: %s danger %v", c.name, e.Race, e.Danger())
			}
		}
	}
}

func TestAnimalExposuresUnknownInputs(t *testing.T) {
	ok := func(Resource) (AnimalComfort, error) { return AnimalComfort{Min: 0, Max: 40}, nil }
	missing := func(Resource) (AnimalComfort, error) { return AnimalComfort{}, errors.New("not shown") }
	none := domain.Known([]DisasterCondition{})
	for name, fn := range map[string]func() error{
		"census": func() error {
			_, err := AnimalExposures([]Resource{"Muffalo"}, domain.Unknown[[]DisasterCondition](), domain.Known(10.0), ok)
			return err
		},
		"temperature": func() error {
			_, err := AnimalExposures([]Resource{"Muffalo"}, none, domain.Unknown[float64](), ok)
			return err
		},
		"range": func() error {
			_, err := AnimalExposures([]Resource{"Muffalo"}, none, domain.Known(10.0), missing)
			return err
		},
	} {
		if err := fn(); !errors.Is(err, ErrAnimalExposure) {
			t.Errorf("%s: %v", name, err)
		}
	}
	if got, err := AnimalExposures(nil, none, domain.Unknown[float64](), ok); err != nil || got != nil {
		t.Errorf("no races: %v %v", got, err)
	}
}
